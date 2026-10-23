namespace WPF_UartAssist.Models;

/// <summary>DoIP 网关连接参数。</summary>
public sealed class DoIpSettings
{
    /// <summary>网关地址（IP 或主机名）。</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>端口，ISO 13400 固定 13400。</summary>
    public string Port { get; set; } = "13400";

    public DoIpSettings Clone() => (DoIpSettings)MemberwiseClone();
}
