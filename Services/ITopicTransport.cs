namespace WPF_UartAssist.Services;

/// <summary>
/// 带主题的接收消息。MQTT 这类按主题分发的协议用它，
/// 普通字节流（串口/TCP/UDP/S7）仍走 <see cref="ITransport"/> 的 DataReceived。
/// </summary>
public sealed class TopicMessage
{
    public TopicMessage(string topic, byte[] payload, int qos, bool retain)
    {
        Topic = topic;
        Payload = payload;
        Qos = qos;
        Retain = retain;
    }

    /// <summary>消息主题。</summary>
    public string Topic { get; }

    /// <summary>消息载荷。</summary>
    public byte[] Payload { get; }

    /// <summary>服务质量等级。</summary>
    public int Qos { get; }

    /// <summary>是否为 broker 保留消息。</summary>
    public bool Retain { get; }
}

/// <summary>支持按主题收发的传输扩展接口。</summary>
public interface ITopicTransport : ITransport
{
    /// <summary>收到带主题的消息。可能在后台线程触发。</summary>
    event Action<TopicMessage>? TopicMessageReceived;

    /// <summary>发布一条消息。topic 为空时使用设置里的默认发布主题。</summary>
    void Publish(string topic, byte[] payload, int qos, bool retain);
}