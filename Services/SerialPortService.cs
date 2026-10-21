using System.IO.Ports;
using System.Text;
using WPF_UartAssist.Models;

namespace WPF_UartAssist.Services;

/// <summary>
/// 串口通信封装。以锁步后台线程从缓冲区取数并发出事件，
/// 避免 DataReceived 在 UI 线程上被高频数据压垮。
/// </summary>
public sealed class SerialPortService : ITransport
{
    private readonly object _syncRoot = new();
    private readonly SerialPortSettings _settings;
    private SerialPort? _serialPort;
    private Thread? _pumpThread;
    private volatile bool _isRunning;
    private long _totalReceived;
    private long _totalSent;

    public SerialPortService(SerialPortSettings settings)
    {
        _settings = settings.Clone();
    }

    /// <summary>接收到原始字节。</summary>
    public event Action<byte[]>? DataReceived;

    /// <summary>接收计数变化（接收字节数）。</summary>
    public event Action<long>? ReceivedCountChanged;

    /// <summary>发送计数变化（发送字节数）。</summary>
    public event Action<long>? SentCountChanged;

    /// <summary>连接状态变化。</summary>
    public event Action<bool>? ConnectionStateChanged;

    /// <summary>运行期错误（拔线、设备占用等）。</summary>
    public event Action<Exception>? ErrorOccurred;

    public bool IsOpen => _serialPort?.IsOpen ?? false;

    public long TotalReceived => Interlocked.Read(ref _totalReceived);

    public long TotalSent => Interlocked.Read(ref _totalSent);

    public string Description => Describe(_settings);

    public string Tag => "串口";

    /// <summary>列出当前可用串口。</summary>
    public static string[] GetPortNames()
    {
        try
        {
            var names = SerialPort.GetPortNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            return names.Length > 0 ? names : [];
        }
        catch
        {
            return [];
        }
    }

    public void Open()
    {
        lock (_syncRoot)
        {
            if (IsOpen)
            {
                throw new InvalidOperationException("串口已处于打开状态。");
            }

            SerialPortSettings settings = _settings;

            var port = new SerialPort(settings.PortName, settings.BaudRate, settings.Parity, settings.DataBits, settings.StopBits)
            {
                Handshake = settings.Handshake,
                ReadTimeout = 500,
                WriteTimeout = 500,
                DtrEnable = true,
                RtsEnable = settings.Handshake == Handshake.RequestToSend
                             || settings.Handshake == Handshake.RequestToSendXOnXOff
            };

            try
            {
                port.Open();
            }
            catch (Exception ex)
            {
                port.Dispose();
                throw new InvalidOperationException($"打开 {settings.PortName} 失败：{ex.Message}", ex);
            }

            _serialPort = port;
            Interlocked.Exchange(ref _totalReceived, 0);
            Interlocked.Exchange(ref _totalSent, 0);
            _isRunning = true;

            _pumpThread = new Thread(PumpLoop)
            {
                IsBackground = true,
                Name = "SerialPortPump"
            };
            _pumpThread.Start();

            ConnectionStateChanged?.Invoke(true);
        }
    }

    public void Close()
    {
        SerialPort? port;

        lock (_syncRoot)
        {
            if (_serialPort is null)
            {
                return;
            }

            _isRunning = false;
            port = _serialPort;
            _serialPort = null;
        }

        // 等待泵线程结束，避免与 Dispose 竞态
        try
        {
            _pumpThread?.Join(500);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(ex);
        }

        try
        {
            if (port?.IsOpen == true)
            {
                port.Close();
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(ex);
        }
        finally
        {
            port?.Dispose();
            _pumpThread = null;
            // 主动关闭不再触发 ConnectionStateChanged：
            // 该事件只表示“意外断开”，避免与正常关闭混淆
        }
    }

    public int Send(byte[] data)
    {
        lock (_syncRoot)
        {
            if (_serialPort is not { IsOpen: true })
            {
                throw new InvalidOperationException("串口未打开，无法发送数据。");
            }

            _serialPort.Write(data, 0, data.Length);
            var total = Interlocked.Add(ref _totalSent, data.Length);
            SentCountChanged?.Invoke(total);
            return data.Length;
        }
    }

    public void DiscardInBuffer()
    {
        lock (_syncRoot)
        {
            if (_serialPort?.IsOpen == true)
            {
                _serialPort.DiscardInBuffer();
            }
        }
    }

    private void PumpLoop()
    {
        var buffer = new byte[4096];

        while (_isRunning)
        {
            SerialPort? port = _serialPort;
            if (port is null)
            {
                break;
            }

            try
            {
                int available = port.BytesToRead;
                if (available > 0)
                {
                    int readCount = Math.Min(available, buffer.Length);
                    int actualRead = port.Read(buffer, 0, readCount);
                    if (actualRead > 0)
                    {
                        var chunk = new byte[actualRead];
                        Array.Copy(buffer, chunk, actualRead);

                        var total = Interlocked.Add(ref _totalReceived, actualRead);
                        ReceivedCountChanged?.Invoke(total);
                        DataReceived?.Invoke(chunk);
                    }

                    continue;
                }
            }
            catch (TimeoutException)
            {
                // 正常的读超时，继续轮询
            }
            catch (InvalidOperationException)
            {
                // 串口已被关闭
                break;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(ex);
                break;
            }

            try
            {
                Thread.Sleep(1);
            }
            catch (ThreadInterruptedException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        Close();
        GC.SuppressFinalize(this);
    }

    /// <summary>校验串口参数组合是否合法。</summary>
    public static string? Validate(SerialPortSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.PortName))
        {
            return "请选择串口。";
        }

        if (settings.BaudRate <= 0)
        {
            return "波特率必须大于 0。";
        }

        if (settings.DataBits is not (5 or 6 or 7 or 8))
        {
            return "数据位仅支持 5/6/7/8。";
        }

        // 5 位数据位无法承载 Mark/Space 校验所需的额外位
        if (settings.DataBits == 5 && settings.Parity is Parity.Mark or Parity.Space)
        {
            return "5 位数据位不支持 Mark/Space 校验。";
        }

        if (settings.DataBits == 5 && settings.StopBits is StopBits.OnePointFive or StopBits.Two)
        {
            return "5 位数据位不支持 1.5/2 停止位，请改用 1 停止位。";
        }

        return null;
    }

    /// <summary>构造用于显示的端口摘要。</summary>
    public static string Describe(SerialPortSettings settings)
        => $"{settings.PortName}  {settings.BaudRate}  {settings.DataBits}{ParityChar(settings.Parity)}{settings.StopBits switch
        {
            StopBits.One => "1",
            StopBits.OnePointFive => "1.5",
            StopBits.Two => "2",
            _ => "1"
        }}";
    private static string ParityChar(Parity parity) => parity switch
    {
        Parity.None => "N",
        Parity.Odd => "O",
        Parity.Even => "E",
        _ => "M"
    };
}
