using System.Net.Sockets;

namespace WPF_UartAssist.Services;

/// <summary>
/// TCP 客户端传输。连接远端后由后台任务循环收数据，
/// 对端关闭或读取异常时触发 ConnectionStateChanged(false)。
/// </summary>
public sealed class TcpClientTransport : ITransport
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

    public TcpClientTransport(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public event Action<byte[]>? DataReceived;

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

    public string Description => $"TCP 客户端 {_host}:{_port}";

    public string Tag => "TCP";

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

        try
        {
            while (!token.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (read <= 0)
                {
                    break; // 对端正常关闭
                }

                var chunk = new byte[read];
                Array.Copy(buffer, chunk, read);

                long total = Interlocked.Add(ref _totalReceived, read);
                ReceivedCountChanged?.Invoke(total);
                DataReceived?.Invoke(chunk);
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
            throw new InvalidOperationException("TCP 未连接，无法发送数据。");
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
