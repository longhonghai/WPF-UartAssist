using System.IO;
using System.Net.Sockets;
using WPF_UartAssist.Models;

namespace WPF_UartAssist.Services;

/// <summary>
/// S7（ISO-on-TCP / S7comm）传输。
/// 打开时先完成 TCP 连接、COTP 连接请求/确认与 PDU 长度协商，之后才能收发业务报文。
/// 收到的数据按 TPKT 长度切帧，剥掉 TPKT/COTP 后把 S7 PDU 交给上层。
/// </summary>
public sealed class S7Transport : ITransport
{
    private const int ConnectTimeoutMs = 5000;
    private const int HandshakeTimeoutMs = 5000;

    private readonly S7Settings _settings;
    private readonly object _syncRoot = new();

    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private bool _closingByUser;
    private long _totalReceived;
    private long _totalSent;

    /// <summary>握手阶段协商到的 PDU 长度（PLC 单次可接受的最大数据量）。</summary>
    private int _negotiatedPduSize = S7Comm.RequestedPduSize;

    public S7Transport(S7Settings settings)
    {
        _settings = settings.Clone();
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

    public string Description =>
        $"S7 {_settings.Host}:{_settings.Port}（机架 {_settings.Rack} 槽位 {_settings.Slot}）";

    public string Tag => "S7";

    /// <summary>协商后的 PDU 长度，用于限制单次读写的数据量。</summary>
    public int NegotiatedPduSize => _negotiatedPduSize;

    public void Open()
    {
        if (!int.TryParse(_settings.Port?.Trim(), out int port) || port is < 1 or > 65535)
        {
            throw new ArgumentException("端口应为 1–65535。");
        }

        if (!int.TryParse(_settings.Rack?.Trim(), out int rack) || rack is < 0 or > 7)
        {
            throw new ArgumentException("机架号应为 0–7。");
        }

        if (!int.TryParse(_settings.Slot?.Trim(), out int slot) || slot is < 0 or > 31)
        {
            throw new ArgumentException("槽位号应为 0–31。");
        }

        var client = new TcpClient { NoDelay = true };

        try
        {
            if (!client.ConnectAsync(_settings.Host, port).Wait(ConnectTimeoutMs))
            {
                client.Dispose();
                throw new TimeoutException($"连接 {_settings.Host}:{port} 超时（{ConnectTimeoutMs / 1000} 秒）");
            }
        }
        catch (AggregateException ex) when (ex.InnerException is not null)
        {
            client.Dispose();
            throw new InvalidOperationException(
                $"连接 {_settings.Host}:{port} 失败：{ex.InnerException.Message}", ex.InnerException);
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
        _negotiatedPduSize = S7Comm.RequestedPduSize;

        // 握手必须在后台读循环启动前同步完成，否则回包没人接收
        try
        {
            PerformHandshake(stream, rack, slot);
        }
        catch
        {
            lock (_syncRoot)
            {
                _stream = null;
                _client = null;
                _cts = null;
            }

            cts.Dispose();
            client.Dispose();
            throw;
        }

        _ = Task.Run(() => ReadLoopAsync(stream, cts.Token));
        ConnectionStateChanged?.Invoke(true);
    }

    /// <summary>
    /// 建立 COTP 连接并协商 PDU 长度。
    /// 顺序：CR → CC → Setup Communication → Ack_Data。
    /// </summary>
    private void PerformHandshake(NetworkStream stream, int rack, int slot)
    {
        byte[] request = S7Comm.BuildConnectionRequest(rack, slot);
        stream.Write(request, 0, request.Length);

        byte[] confirm = ReadFrame(stream);

        // TPKT 占前 4 字节，COTP 从索引 4 开始：LI(4) + PDU 类型(5)
        // 0xE0 = Connection Request，0xD0 = Connection Confirm
        if (confirm.Length < 6 || confirm[5] != 0xD0)
        {
            byte code = confirm.Length > 5 ? confirm[5] : (byte)0;
            throw new InvalidOperationException(
                $"PLC 拒绝了 COTP 连接请求（PDU 类型 0x{code:X2}）。请确认机架/槽位是否与 PLC 实际一致。");
        }

        // 协商 PDU 长度：收到 Ack_Data 后取回参数区里的实际值
        byte[] setup = S7Comm.WrapCotp(S7Comm.BuildSetupCommunication(0x0001));
        stream.Write(setup, 0, setup.Length);

        byte[] setupAck = ReadFrame(stream);
        byte[]? pdu = S7Comm.UnwrapCotp(setupAck);

        if (pdu is { Length: >= S7Comm.AckHeaderSize })
        {
            int paramLen = (pdu[6] << 8) | pdu[7];

            // 参数区从 Ack 头之后开始，首字节是功能码
            if (paramLen >= 8 && pdu[S7Comm.AckHeaderSize] == S7Comm.FuncSetupCommunication)
            {
                int dataOffset = S7Comm.AckHeaderSize + paramLen;
                int lenBytes = pdu[dataOffset - 2];

                if (lenBytes is >= 1 and <= 2 && pdu.Length >= dataOffset + lenBytes)
                {
                    int size = 0;
                    for (int i = 0; i < lenBytes; i++)
                    {
                        size = (size << 8) | pdu[dataOffset + i];
                    }

                    if (size is >= 240 and <= 960)
                    {
                        _negotiatedPduSize = size;
                    }
                }
            }
        }
    }

    /// <summary>握手阶段同步读取一个完整 TPKT 帧。</summary>
    private static byte[] ReadFrame(NetworkStream stream)
    {
        var buffer = new List<byte>(256);
        var chunk = new byte[512];

        using var timeout = new CancellationTokenSource(HandshakeTimeoutMs);

        try
        {
            while (true)
            {
                int read = stream.Read(chunk, 0, chunk.Length);
                if (read <= 0)
                {
                    throw new IOException("PLC 在握手过程中关闭了连接。");
                }

                buffer.AddRange(chunk.AsSpan(0, read));

                int consumed = S7Comm.TryReadTpkt(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buffer), out byte[] frame);

                if (consumed > 0)
                {
                    return frame;
                }

                if (buffer.Count > 8192)
                {
                    throw new IOException("PLC 返回的握手数据不是合法的 TPKT 帧。");
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"PLC 未在 {HandshakeTimeoutMs / 1000} 秒内响应握手。");
        }
        catch (IOException ex)
        {
            throw new IOException($"PLC 握手失败：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 后台收数据：按 TPKT 长度切帧，剥掉 TPKT/COTP 后回调 S7 PDU。
    /// </summary>
    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        var pending = new List<byte>(1024);

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

                // TCP 是字节流，可能粘包/拆包，逐帧切分
                while (pending.Count >= 7)
                {
                    int consumed = S7Comm.TryReadTpkt(
                        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pending), out byte[] frame);

                    if (consumed == 0)
                    {
                        break;
                    }

                    pending.RemoveRange(0, consumed);

                    byte[]? pdu = S7Comm.UnwrapCotp(frame);
                    if (pdu is not null)
                    {
                        DataReceived?.Invoke(pdu);
                    }
                }

                // 缓冲区异常膨胀说明流已错乱，直接丢弃避免内存泄漏
                if (pending.Count > 64 * 1024)
                {
                    pending.Clear();
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
            throw new InvalidOperationException("S7 未连接，无法发送数据。");
        }

        // 裸 S7 PDU 自动补 TPKT/COTP，完整帧原样发出
        byte[] payload = S7Comm.PrepareOutbound(data);
        stream.Write(payload, 0, payload.Length);

        long total = Interlocked.Add(ref _totalSent, payload.Length);
        SentCountChanged?.Invoke(total);
        return payload.Length;
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