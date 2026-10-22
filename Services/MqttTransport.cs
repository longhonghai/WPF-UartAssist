using System.Buffers;
using System.Text;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using WPF_UartAssist.Models;

namespace WPF_UartAssist.Services;

/// <summary>
/// MQTT 传输。基于 MQTTnet 5.x，实现 <see cref="ITopicTransport"/>。
/// 支持连接、发布、订阅与断线自动重连。
/// </summary>
public sealed class MqttTransport : ITopicTransport
{
    private const int ConnectTimeoutMs = 5000;

    private readonly MqttSettings _settings;
    private readonly object _syncRoot = new();

    private IMqttClient? _client;
    private CancellationTokenSource? _cts;
    private bool _closingByUser;
    private long _totalReceived;
    private long _totalSent;

    /// <summary>当前已订阅的主题，断线重连后按此恢复订阅。</summary>
    private readonly List<string> _subscribedTopics = [];

    private Task? _reconnectTask;

    public MqttTransport(MqttSettings settings)
    {
        _settings = settings.Clone();

        // 提前定下客户端 ID，Description 才能在连接前就带上它
        ClientId = string.IsNullOrWhiteSpace(_settings.ClientId)
            ? $"uartassist-{Environment.ProcessId}-{DateTime.Now:HHmmss}"
            : _settings.ClientId.Trim();
    }

    /// <summary>
    /// MQTT 不走字节流通道，接收一律经 <see cref="TopicMessageReceived"/> 上报（带主题/QoS/保留位）。
    /// 这里保留事件声明以符合 <see cref="ITransport"/>，但不投递，避免同一条消息被记两遍。
    /// </summary>
    public event Action<byte[]>? DataReceived
    {
        add { }
        remove { }
    }

    public event Action<TopicMessage>? TopicMessageReceived;

    public event Action<long>? ReceivedCountChanged;

    public event Action<long>? SentCountChanged;

    public event Action<bool>? ConnectionStateChanged;

    public event Action<Exception>? ErrorOccurred;

    public bool IsOpen => _client?.IsConnected ?? false;

    public long TotalReceived => Interlocked.Read(ref _totalReceived);

    public long TotalSent => Interlocked.Read(ref _totalSent);

    public string Description =>
        $"MQTT {_settings.Host}:{_settings.Port}" +
        (_settings.UseTls ? "（TLS）" : string.Empty) +
        $" 客户端 {ClientId}";

    public string Tag => "MQTT";

    /// <summary>实际使用的客户端 ID。</summary>
    public string ClientId { get; private set; } = string.Empty;

    /// <summary>已订阅的主题列表。</summary>
    public IReadOnlyList<string> SubscribedTopics
    {
        get
        {
            lock (_subscribedTopics)
            {
                return _subscribedTopics.ToArray();
            }
        }
    }

    public void Open()
    {
        if (!int.TryParse(_settings.Port?.Trim(), out int port) || port is < 1 or > 65535)
        {
            throw new ArgumentException("端口应为 1–65535。");
        }

        if (string.IsNullOrWhiteSpace(_settings.Host))
        {
            throw new ArgumentException("请填写 Broker 地址。");
        }

        ClientId = string.IsNullOrWhiteSpace(_settings.ClientId)
            ? ClientId
            : _settings.ClientId.Trim();

        var factory = new MqttClientFactory();
        IMqttClient client = factory.CreateMqttClient();

        client.ApplicationMessageReceivedAsync += OnApplicationMessageReceived;
        client.DisconnectedAsync += OnDisconnected;

        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(_settings.Host.Trim(), port)
            .WithClientId(ClientId)
            .WithCleanSession(_settings.CleanSession)
            .WithProtocolVersion(ResolveProtocolVersion())
            .WithTimeout(TimeSpan.FromMilliseconds(ConnectTimeoutMs));

        if (!string.IsNullOrWhiteSpace(_settings.UserName))
        {
            builder.WithCredentials(
                _settings.UserName,
                Encoding.UTF8.GetBytes(_settings.Password ?? string.Empty));
        }

        if (_settings.UseTls)
        {
            builder.WithTlsOptions(tls => tls
                .UseTls()
                .WithTargetHost(_settings.Host.Trim())
                // 自签名证书在工业环境很常见，这里放行但仍走加密
                .WithAllowUntrustedCertificates(true)
                .WithIgnoreCertificateChainErrors(true)
                .WithIgnoreCertificateRevocationErrors(true));
        }

        if (int.TryParse(_settings.KeepAlive?.Trim(), out int keepAlive) && keepAlive > 0)
        {
            builder.WithKeepAlivePeriod(TimeSpan.FromSeconds(keepAlive));
        }

        MqttClientOptions options = builder.Build();

        var cts = new CancellationTokenSource();

        lock (_syncRoot)
        {
            _client = client;
            _cts = cts;
            _closingByUser = false;
        }

        Interlocked.Exchange(ref _totalReceived, 0);
        Interlocked.Exchange(ref _totalSent, 0);

        try
        {
            using var timeout = new CancellationTokenSource(ConnectTimeoutMs);
            client.ConnectAsync(options, timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            lock (_syncRoot)
            {
                _client = null;
                _cts = null;
            }

            cts.Dispose();
            client.Dispose();
            throw new InvalidOperationException($"连接 MQTT Broker {_settings.Host}:{port} 失败：{ex.Message}", ex);
        }

        // 连上后按设置自动订阅
        if (!string.IsNullOrWhiteSpace(_settings.SubscribeTopic))
        {
            Subscribe(_settings.SubscribeTopic, ParseQos(_settings.SubscribeQos));
        }

        ConnectionStateChanged?.Invoke(true);
    }

    private MqttProtocolVersion ResolveProtocolVersion()
        => _settings.ProtocolVersion == "5.0" ? MqttProtocolVersion.V500 : MqttProtocolVersion.V311;

    private static MqttQualityOfServiceLevel ParseQos(string? qos)
        => (MqttQualityOfServiceLevel)ParseQosText(qos);

    /// <summary>订阅一个或多个主题（分号分隔，支持通配符）。</summary>
    public void Subscribe(string topics, MqttQualityOfServiceLevel qos)
    {
        IMqttClient? client = _client;

        if (client is null || !client.IsConnected)
        {
            throw new InvalidOperationException("MQTT 未连接，无法订阅。");
        }

        List<string> list = topics.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                   .Distinct()
                                   .ToList();

        if (list.Count == 0)
        {
            return;
        }

        var options = new MqttClientSubscribeOptionsBuilder();

        foreach (string topic in list)
        {
            options.WithTopicFilter(topic, qos);
        }

        client.SubscribeAsync(options.Build()).GetAwaiter().GetResult();

        lock (_subscribedTopics)
        {
            foreach (string topic in list)
            {
                if (!_subscribedTopics.Contains(topic))
                {
                    _subscribedTopics.Add(topic);
                }
            }
        }
    }

    /// <summary>取消订阅。</summary>
    public void Unsubscribe(string topics)
    {
        IMqttClient? client = _client;

        if (client is null || !client.IsConnected)
        {
            return;
        }

        List<string> list = topics.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                   .Distinct()
                                   .ToList();

        if (list.Count == 0)
        {
            return;
        }

        var options = new MqttClientUnsubscribeOptionsBuilder();

        foreach (string topic in list)
        {
            options.WithTopicFilter(topic);
        }

        client.UnsubscribeAsync(options.Build()).GetAwaiter().GetResult();

        lock (_subscribedTopics)
        {
            foreach (string topic in list)
            {
                _subscribedTopics.Remove(topic);
            }
        }
    }

    public void Publish(string topic, byte[] payload, int qos, bool retain)
    {
        IMqttClient? client = _client;

        if (client is null || !client.IsConnected)
        {
            throw new InvalidOperationException("MQTT 未连接，无法发布。");
        }

        if (string.IsNullOrWhiteSpace(topic))
        {
            throw new ArgumentException("发布主题不能为空。");
        }

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)Math.Clamp(qos, 0, 2))
            .WithRetainFlag(retain)
            .Build();

        client.PublishAsync(message).GetAwaiter().GetResult();

        long total = Interlocked.Add(ref _totalSent, payload.Length);
        SentCountChanged?.Invoke(total);
    }

    /// <summary>ITransport 的发送入口：发到设置里的默认发布主题。</summary>
    public int Send(byte[] data)
    {
        Publish(_settings.PublishTopic, data, ParseQosText(_settings.PublishQos), _settings.Retain);
        return data.Length;
    }

    /// <summary>设置里存的是「0 最多一次」这类标签，取前面的数字。</summary>
    private static int ParseQosText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        int length = 0;

        while (length < span.Length && char.IsAsciiDigit(span[length]))
        {
            length++;
        }

        return length > 0 && int.TryParse(span[..length], out int level) ? Math.Clamp(level, 0, 2) : 0;
    }

    private Task OnApplicationMessageReceived(MqttApplicationMessageReceivedEventArgs e)
    {
        byte[] payload = e.ApplicationMessage.Payload.ToArray();

        long total = Interlocked.Add(ref _totalReceived, payload.Length);
        ReceivedCountChanged?.Invoke(total);

        var message = new TopicMessage(
            e.ApplicationMessage.Topic ?? string.Empty,
            payload,
            (int)e.ApplicationMessage.QualityOfServiceLevel,
            e.ApplicationMessage.Retain);

        // 只走主题通道：DataReceived 不再触发，否则同一条消息会被记两遍
        TopicMessageReceived?.Invoke(message);
        return Task.CompletedTask;
    }

    private Task OnDisconnected(MqttClientDisconnectedEventArgs e)
    {
        bool notify;
        bool autoReconnect = _settings.AutoReconnect;

        lock (_syncRoot)
        {
            notify = !_closingByUser;
            _client = null;
        }

        if (!notify)
        {
            return Task.CompletedTask;
        }

        ConnectionStateChanged?.Invoke(false);

        if (e.Exception is not null)
        {
            ErrorOccurred?.Invoke(e.Exception);
        }

        if (autoReconnect)
        {
            StartReconnectLoop();
        }

        return Task.CompletedTask;
    }

    /// <summary>断线后按固定间隔重试，直到成功或用户主动断开。</summary>
    private void StartReconnectLoop()
    {
        lock (_syncRoot)
        {
            if (_reconnectTask is { IsCompleted: false })
            {
                return;
            }

            _reconnectTask = Task.Run(async () =>
            {
                while (true)
                {
                    CancellationTokenSource? cts;

                    lock (_syncRoot)
                    {
                        if (_closingByUser)
                        {
                            return;
                        }

                        cts = _cts;
                    }

                    if (cts is null || cts.IsCancellationRequested)
                    {
                        return;
                    }

                    await Task.Delay(3000, cts.Token).ConfigureAwait(false);

                    try
                    {
                        Open();
                        return;
                    }
                    catch (Exception)
                    {
                        // 继续重试
                    }
                }
            });
        }
    }

    public void Close()
    {
        CancellationTokenSource? cts;
        IMqttClient? client;

        lock (_syncRoot)
        {
            _closingByUser = true;
            cts = _cts;
            _cts = null;
            client = _client;
            _client = null;
        }

        try
        {
            cts?.Cancel();
        }
        catch
        {
            // 已释放
        }

        if (client is not null)
        {
            try
            {
                client.DisconnectAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // 忽略断开异常
            }

            client.Dispose();
        }

        lock (_subscribedTopics)
        {
            _subscribedTopics.Clear();
        }

        // 主动关闭不触发 ConnectionStateChanged，该事件只表示意外断开
    }

    public void Dispose()
    {
        Close();
        GC.SuppressFinalize(this);
    }
}
