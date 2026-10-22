namespace WPF_UartAssist.Models;

/// <summary>
/// OPC UA 连接参数。既是客户端连接目标，也是内置服务端的监听设置。
/// </summary>
public sealed class OpcUaSettings
{
    // ===== 客户端 =====

    /// <summary>端点地址，如 opc.tcp://127.0.0.1:4840。</summary>
    public string EndpointUrl { get; set; } = "opc.tcp://127.0.0.1:48400";

    /// <summary>是否用用户名/密码登录（否则匿名）。</summary>
    public bool UseSecurity { get; set; }

    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary>要读写的节点，如 ns=2;s=Demo.Text。</summary>
    public string NodeId { get; set; } = "ns=2;s=Demo.Text";

    /// <summary>写入值（按文本输入，按节点类型转换）。</summary>
    public string WriteValue { get; set; } = string.Empty;

    // ===== 服务端 =====

    /// <summary>内置服务端监听端口。默认 48400：4840 是 OPC UA 标准端口，常被 LDS 等软件占用。</summary>
    public string ServerPort { get; set; } = "48400";

    public OpcUaSettings Clone() => (OpcUaSettings)MemberwiseClone();
}
