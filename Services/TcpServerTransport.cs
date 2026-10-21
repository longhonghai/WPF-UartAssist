using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace WPF_UartAssist.Services;

/// <summary>
/// TCP 服务端传输。监听本地端口，接受多个客户端连接；
/// 发送时广播给所有在线客户端（没有客户端时报错）。
/// </summary>
public sealed class TcpServerTransport : ITransport
{
    private readonly int _port;
    private readonly ConcurrentDictionary<TcpClient, NetworkStream> _clients = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private bool _closingByUser;
    private long _totalReceived;
    private long _totalSent;

    public TcpServerTransport(int port)
    {
        _port = port;
    }

    public event Action<byte[]>? DataReceived;

    public event Action<long>? ReceivedCountChanged;

    public event Action<long>? SentCountChanged;

    public event Action<bool>? ConnectionStateChanged;

    public event Action<Exception>? ErrorOccurred;

    public bool IsOpen => _listener is not null;

    public long TotalReceived => Interlocked.Read(ref _totalReceived);

    public long TotalSent => Interlocked.Read(ref _totalSent);

    /// <summary>当前在线客户端数量。</summary>
    public int ClientCount => _clients.Count;

    public string Description => $"TCP 服务端 监听 {_port}";

    public string Tag => "TCP";

    public void Open()
    {
        var listener = new TcpListener(IPAddress.Any, _port);

        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            listener.Stop();
            throw new InvalidOperationException($"监听端口 {_port} 失败：{ex.Message}", ex);
        }

        _listener = listener;
        _cts = new CancellationTokenSource();
        _closingByUser = false;

        Interlocked.Exchange(ref _totalReceived, 0);
        Interlocked.Exchange(ref _totalSent, 0);

        _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
        ConnectionStateChanged?.Invoke(true);
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                client.NoDelay = true;

                NetworkStream stream = client.GetStream();
                _clients[client] = stream;

                _ = Task.Run(() => ReadLoopAsync(client, stream, token));
            }
        }
        catch (OperationCanceledException)
        {
            // 正常关闭
        }
        catch (Exception ex)
        {
            if (!_closingByUser)
            {
                ErrorOccurred?.Invoke(ex);
            }
        }
        finally
        {
            if (!_closingByUser)
            {
                ConnectionStateChanged?.Invoke(false);
            }
        }
    }

    private async Task ReadLoopAsync(TcpClient client, NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[8192];

        try
        {
            while (!token.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
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
            // 正常关闭
        }
        catch (Exception ex)
        {
            if (!_closingByUser)
            {
                // 单个客户端异常不影响其它连接，也不终止监听
                ErrorOccurred?.Invoke(ex);
            }
        }
        finally
        {
            _clients.TryRemove(client, out _);
            try
            {
                client.Dispose();
            }
            catch
            {
                // 忽略
            }
        }
    }

    public int Send(byte[] data)
    {
        if (_clients.IsEmpty)
        {
            throw new InvalidOperationException("还没有客户端连接，无法发送。");
        }

        int sent = 0;
        int failed = 0;

        foreach (var pair in _clients)
        {
            try
            {
                pair.Value.Write(data, 0, data.Length);
                sent++;
            }
            catch
            {
                failed++;
                _clients.TryRemove(pair.Key, out _);
                try
                {
                    pair.Key.Dispose();
                }
                catch
                {
                    // 忽略
                }
            }
        }

        if (sent == 0)
        {
            throw new InvalidOperationException($"所有客户端都已断开（{failed} 个），发送失败。");
        }

        long total = Interlocked.Add(ref _totalSent, data.Length * sent);
        SentCountChanged?.Invoke(total);

        // 返回广播的字节总量，日志里显示“发给 N 个客户端共 M 字节”
        return data.Length * sent;
    }

    public void Close()
    {
        _closingByUser = true;

        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // 已释放
        }

        try
        {
            _listener?.Stop();
        }
        catch
        {
            // 忽略
        }

        _listener = null;
        _cts?.Dispose();
        _cts = null;

        foreach (var pair in _clients)
        {
            try
            {
                pair.Key.Dispose();
            }
            catch
            {
                // 忽略
            }
        }

        _clients.Clear();

        // 主动关闭不触发 ConnectionStateChanged，该事件只表示意外断开
    }

    public void Dispose()
    {
        Close();
        GC.SuppressFinalize(this);
    }
}
