namespace WPF_UartAssist.Models;

/// <summary>
/// S7（ISO-on-TCP / S7comm）连接参数。
/// 机架与槽位决定 COTP 握手时的目标 TSAP，S7-1200/1500 通常为 0/1，S7-300/400 为 0/2。
/// </summary>
public sealed class S7Settings
{
    /// <summary>PLC 地址（IP 或主机名）。</summary>
    public string Host { get; set; } = "192.168.0.1";

    /// <summary>ISO-on-TCP 固定端口 102。</summary>
    public string Port { get; set; } = "102";

    /// <summary>机架号，一般为 0。</summary>
    public string Rack { get; set; } = "0";

    /// <summary>槽位号。</summary>
    public string Slot { get; set; } = "1";

    public S7Settings Clone() => (S7Settings)MemberwiseClone();
}