namespace WPF_UartAssist.Models;

/// <summary>
/// MQTT（MQTT 3.1.1 / 5.0）连接与收发参数。
/// MQTT 是主题发布/订阅模型，不是字节流：发布走 PublishTopic，接收靠订阅主题。
/// </summary>
public sealed class MqttSettings
{
    // ===== 连接 =====

    /// <summary>Broker 地址（IP 或主机名）。</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>Broker 端口，默认 1883（TLS 为 8883）。</summary>
    public string Port { get; set; } = "1883";

    /// <summary>客户端 ID。留空时自动生成一个带序号的 ID。</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>用户名，留空表示匿名。</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>密码，留空表示不带密码。</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>是否启用 TLS。</summary>
    public bool UseTls { get; set; }

    /// <summary>是否使用干净的会话（不保留订阅与排队消息）。</summary>
    public bool CleanSession { get; set; } = true;

    /// <summary>协议版本：3.1.1 或 5.0。</summary>
    public string ProtocolVersion { get; set; } = "3.1.1";

    /// <summary>保活周期（秒），0 表示不保活。</summary>
    public string KeepAlive { get; set; } = "60";

    // ===== 主题 =====

    /// <summary>发布主题，发送区的内容发到这里。</summary>
    public string PublishTopic { get; set; } = "uart/tx";

    /// <summary>订阅主题，支持 + 与 # 通配符。多个用分号分隔。</summary>
    public string SubscribeTopic { get; set; } = "uart/rx";

    /// <summary>发布 QoS：0 最多一次 / 1 至少一次 / 2 恰好一次。</summary>
    public string PublishQos { get; set; } = "0 最多一次";

    /// <summary>订阅 QoS。</summary>
    public string SubscribeQos { get; set; } = "0 最多一次";

    /// <summary>发布时是否设置 retain 标志。</summary>
    public bool Retain { get; set; }

    /// <summary>断线后是否自动重连。</summary>
    public bool AutoReconnect { get; set; } = true;

    public MqttSettings Clone() => (MqttSettings)MemberwiseClone();
}