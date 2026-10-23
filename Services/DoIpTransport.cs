using System.Net.Sockets;

namespace WPF_UartAssist.Services;

/// <summary>
/// DoIP（ISO 13400-2）传输：TCP 连到诊断网关的 13400 端口，
/// 按 8 字节头切帧后逐帧上报（不投递原始字节流，避免日志重复）。
/// </summary>
public sealed class DoIpTransport : ITransport
{
    private const int ConnectTimeoutMs = 5000;

    private readonly string _host;
    private readonly int _port;
    private readonly object _syncRoot = new();

    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private bool _closingByUser;
    private long _totalReceived;
    private long _totalSent;

    public DoIpTransport(string host, int port)
    {
        _host = host;
        _port = port;
    }

    /// <summary>解析出的完整 DoIP 帧。</summary>
    public event Action<DoIpFrame>? FrameReceived;

    /// <summary>收到的数据不是合法 DoIP 帧时的提示。</summary>
    public event Action<string>? FrameError;

    /// <summary>DoIP 走帧通道，不再投递原始字节流，否则同一帧会记两遍。</summary>
    public event Action<byte[]>? DataReceived
    {
        add { }
        remove { }
    }

    public event Action<long>? ReceivedCountChanged;

    public event Action<long>? SentCountChanged;

    public event Action<bool>? ConnectionStateChanged;

    public event Action<Exception>? ErrorOccurred;

    public bool IsOpen
    {
        get
        {
            lock (_syncRoot)
            {
                return _stream is not null;
            }
        }
    }

    public long TotalReceived => Interlocked.Read(ref _totalReceived);

    public long TotalSent => Interlocked.Read(ref _totalSent);

    public string Description => $"DoIP 网关 {_host}:{_port}";

    public string Tag => "DoIP";

    public void Open()
    {
        var client = new TcpClient { NoDelay = true };

        try
        {
            // 连接超时要可控：超时后必须 Dispose 才能取消未完成的连接
            if (!client.ConnectAsync(_host, _port).Wait(ConnectTimeoutMs))
            {
                client.Dispose();
                throw new TimeoutException($"连接 {_host}:{_port} 超时（{ConnectTimeoutMs / 1000} 秒）");
            }
        }
        catch (AggregateException ex) when (ex.InnerException is not null)
        {
            client.Dispose();
            throw new InvalidOperationException(
                $"连接 {_host}:{_port} 失败：{ex.InnerException.Message}", ex.InnerException);
        }
        catch (TimeoutException)
        {
            client.Dispose();
            throw;
        }

        var stream = client.GetStream();
        var cts = new CancellationTokenSource();

        lock (_syncRoot)
        {
            _client = client;
            _stream = stream;
            _cts = cts;
            _closingByUser = false;
        }

        Interlocked.Exchange(ref _totalReceived, 0);
        Interlocked.Exchange(ref _totalSent, 0);

        _ = Task.Run(() => ReadLoopAsync(stream, cts.Token));
        ConnectionStateChanged?.Invoke(true);
    }

    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[8192];
        var pending = new List<byte>(4096);

        try
        {
            while (!token.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (read <= 0)
                {
                    break; // 对端正常关闭
                }

                long total = Interlocked.Add(ref _totalReceived, read);
                ReceivedCountChanged?.Invoke(total);

                pending.AddRange(buffer.AsSpan(0, read).ToArray());

                // 一个 TCP 段里可能有多帧，粘包要循环切完
                while (true)
                {
                    bool parsed = DoIp.TryParse(
                        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pending),
                        out DoIpFrame? frame,
                        out int consumed,
                        out string? error);

                    if (error is not null)
                    {
                        FrameError?.Invoke(error);
                    }

                    if (consumed > 0)
                    {
                        pending.RemoveRange(0, consumed);
                    }

                    if (!parsed)
                    {
                        break;
                    }

                    if (frame is not null)
                    {
                        FrameReceived?.Invoke(frame);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 主动关闭，正常路径
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(ex);
        }
        finally
        {
            HandleRemoteClosed();
        }
    }

    private void HandleRemoteClosed()
    {
        bool notify;

        lock (_syncRoot)
        {
            notify = !_closingByUser && _stream is not null;
            _stream = null;
            _client?.Dispose();
            _client = null;
            _cts?.Dispose();
            _cts = null;
        }

        if (notify)
        {
            ConnectionStateChanged?.Invoke(false);
        }
    }

    public int Send(byte[] data)
    {
        NetworkStream? stream;

        lock (_syncRoot)
        {
            stream = _stream;
        }

        if (stream is null)
        {
            throw new InvalidOperationException("DoIP 未连接，无法发送数据。");
        }

        stream.Write(data, 0, data.Length);
        long total = Interlocked.Add(ref _totalSent, data.Length);
        SentCountChanged?.Invoke(total);
        return data.Length;
    }

    public void Close()
    {
        CancellationTokenSource? cts;

        lock (_syncRoot)
        {
            if (_stream is null && _client is null)
            {
                return;
            }

            _closingByUser = true;
            cts = _cts;
            _cts = null;
        }

        try
        {
            cts?.Cancel();
        }
        catch
        {
            // 已释放
        }

        lock (_syncRoot)
        {
            try
            {
                _stream?.Dispose();
            }
            catch
            {
                // 忽略关闭异常
            }

            _stream = null;
            _client?.Dispose();
            _client = null;
        }

        // 主动关闭不触发 ConnectionStateChanged，该事件只表示意外断开
    }

    public void Dispose()
    {
        Close();
        GC.SuppressFinalize(this);
    }
}
