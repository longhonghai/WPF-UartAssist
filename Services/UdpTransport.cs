using System.Net;
using System.Net.Sockets;

namespace WPF_UartAssist.Services;

/// <summary>
/// UDP 传输。绑定本地端口接收，发送到指定的远端地址。
/// 一个 datagram 触发一次 DataReceived，保留报文边界（不合并）。
/// </summary>
public sealed class UdpTransport : ITransport
{
    private readonly int _localPort;
    private readonly string _remoteHost;
    private readonly int _remotePort;

    private UdpClient? _udp;
    private IPEndPoint? _remoteEndPoint;
    private CancellationTokenSource? _cts;
    private bool _closingByUser;
    private long _totalReceived;
    private long _totalSent;

    public UdpTransport(int localPort, string remoteHost, int remotePort)
    {
        _localPort = localPort;
        _remoteHost = remoteHost;
        _remotePort = remotePort;
    }

    public event Action<byte[]>? DataReceived;

    public event Action<long>? ReceivedCountChanged;

    public event Action<long>? SentCountChanged;

    public event Action<bool>? ConnectionStateChanged;

    public event Action<Exception>? ErrorOccurred;

    public bool IsOpen => _udp is not null;

    public long TotalReceived => Interlocked.Read(ref _totalReceived);

    public long TotalSent => Interlocked.Read(ref _totalSent);

    public string Description => _localPort > 0
        ? $"UDP 本地 {_localPort} → {_remoteHost}:{_remotePort}"
        : $"UDP → {_remoteHost}:{_remotePort}";

    public string Tag => "UDP";

    public void Open()
    {
        IPAddress remoteAddress = ResolveHost(_remoteHost);

        var udp = new UdpClient(new IPEndPoint(IPAddress.Any, _localPort));

        _udp = udp;
        _remoteEndPoint = new IPEndPoint(remoteAddress, _remotePort);
        _cts = new CancellationTokenSource();
        _closingByUser = false;

        Interlocked.Exchange(ref _totalReceived, 0);
        Interlocked.Exchange(ref _totalSent, 0);

        _ = Task.Run(() => ReceiveLoopAsync(udp, _cts.Token));
        ConnectionStateChanged?.Invoke(true);
    }

    private static IPAddress ResolveHost(string host)
    {
        if (IPAddress.TryParse(host, out IPAddress? address))
        {
            return address;
        }

        try
        {
            IPAddress[] addresses = Dns.GetHostAddresses(host);
            return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                   ?? addresses.FirstOrDefault()
                   ?? throw new InvalidOperationException($"无法解析主机名 {host}");
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException($"无法解析主机名 {host}：{ex.Message}", ex);
        }
    }

    private async Task ReceiveLoopAsync(UdpClient udp, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                UdpReceiveResult result = await udp.ReceiveAsync(token).ConfigureAwait(false);
                byte[] data = result.Buffer;

                if (data.Length == 0)
                {
                    continue;
                }

                // 记录对端，便于未配置远端时也能回给最近一次发来数据的地址
                _remoteEndPoint ??= result.RemoteEndPoint;

                long total = Interlocked.Add(ref _totalReceived, data.Length);
                ReceivedCountChanged?.Invoke(total);
                DataReceived?.Invoke(data);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常关闭
        }
        catch (ObjectDisposedException)
        {
            // 端口已释放
        }
        catch (Exception ex)
        {
            if (!_closingByUser)
            {
                ErrorOccurred?.Invoke(ex);
            }
        }
    }

    public int Send(byte[] data)
    {
        UdpClient? udp = _udp;
        IPEndPoint? remote = _remoteEndPoint;

        if (udp is null || remote is null)
        {
            throw new InvalidOperationException("UDP 未打开，无法发送数据。");
        }

        int written = udp.Send(data, data.Length, remote);
        long total = Interlocked.Add(ref _totalSent, written);
        SentCountChanged?.Invoke(total);
        return written;
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
            _udp?.Dispose();
        }
        catch
        {
            // 忽略
        }

        _udp = null;
        _cts?.Dispose();
        _cts = null;

        // 主动关闭不触发 ConnectionStateChanged，该事件只表示意外断开
    }

    public void Dispose()
    {
        Close();
        GC.SuppressFinalize(this);
    }
}
