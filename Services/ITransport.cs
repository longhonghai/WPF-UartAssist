namespace WPF_UartAssist.Services;

/// <summary>
/// 通讯传输抽象。串口与 TCP/UDP 都实现这一层，
/// ViewModel 只跟接口打交道，收发管道（队列/日志/统计）与传输无关。
/// </summary>
public interface ITransport : IDisposable
{
    /// <summary>收到原始字节。可能由后台线程触发，订阅方需自行保证线程安全。</summary>
    event Action<byte[]>? DataReceived;

    /// <summary>累计接收字节数变化。</summary>
    event Action<long>? ReceivedCountChanged;

    /// <summary>累计发送字节数变化。</summary>
    event Action<long>? SentCountChanged;

    /// <summary>连接状态变化（TCP 对端断开、监听停止等）。</summary>
    event Action<bool>? ConnectionStateChanged;

    /// <summary>运行期错误。</summary>
    event Action<Exception>? ErrorOccurred;

    bool IsOpen { get; }

    long TotalReceived { get; }

    long TotalSent { get; }

    /// <summary>用于状态栏/日志的连接描述，如 “COM3 115200bps 8N1” 或 “TCP 客户端 127.0.0.1:8080”。</summary>
    string Description { get; }

    /// <summary>来源标签，多路同时收发时用于区分日志（串口 / TCP / UDP）。</summary>
    string Tag { get; }

    void Open();

    void Close();

    int Send(byte[] data);
}
