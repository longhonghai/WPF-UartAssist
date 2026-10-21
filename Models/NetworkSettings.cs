namespace WPF_UartAssist.Models;

/// <summary>网络通讯方式。</summary>
public enum NetworkMode
{
    TcpClient,
    TcpServer,
    Udp
}

/// <summary>
/// TCP/UDP 连接参数。地址/端口用字符串保存，避免输入过程中的中间态被强转。
/// </summary>
public sealed class NetworkSettings
{
    public NetworkMode Mode { get; set; } = NetworkMode.TcpClient;

    /// <summary>远端地址：TCP 客户端要连的地址，UDP 的发送目标。</summary>
    public string RemoteHost { get; set; } = "127.0.0.1";

    /// <summary>远端端口。</summary>
    public string RemotePort { get; set; } = "8080";

    /// <summary>本地监听端口：TCP 服务端必填，UDP 可留 0 表示随机端口。</summary>
    public string LocalPort { get; set; } = "8080";

    public NetworkSettings Clone() => (NetworkSettings)MemberwiseClone();
}
