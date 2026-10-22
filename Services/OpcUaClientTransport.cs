using Opc.Ua;
using Opc.Ua.Client;
using WPF_UartAssist.Models;

namespace WPF_UartAssist.Services;

/// <summary>
/// OPC UA 客户端会话。OPC UA 是服务模型不是字节流，
/// 因此 <see cref="Send"/> 不可用，读写走 <see cref="ReadNode"/> / <see cref="WriteNode"/>。
/// </summary>
public sealed class OpcUaClientTransport : ITransport
{
    private const int DiscoveryTimeoutMs = 8000;

    private readonly OpcUaSettings _settings;
    private readonly object _syncRoot = new();

    private Opc.Ua.Client.Session? _session;
    private long _totalReceived;
    private long _totalSent;

    public OpcUaClientTransport(OpcUaSettings settings)
    {
        _settings = settings.Clone();
    }

    /// <summary>OPC UA 不走字节流通道，读取结果由界面直接展示。</summary>
    public event Action<byte[]>? DataReceived
    {
        add { }
        remove { }
    }

    public event Action<long>? ReceivedCountChanged;

    public event Action<long>? SentCountChanged;

    public event Action<bool>? ConnectionStateChanged;

    /// <summary>读写失败由调用方直接捕获处理，这里保留声明以满足接口。</summary>
    public event Action<Exception>? ErrorOccurred
    {
        add { }
        remove { }
    }

    public bool IsOpen => _session?.Connected ?? false;

    public long TotalReceived => Interlocked.Read(ref _totalReceived);

    public long TotalSent => Interlocked.Read(ref _totalSent);

    public string Description
    {
        get
        {
            string security = _settings.UseSecurity ? "用户名" : "匿名";
            return $"OPC UA 客户端 {_settings.EndpointUrl}（{security}）";
        }
    }

    public string Tag => "OPC UA";

    /// <summary>会话超时（毫秒）。</summary>
    public uint SessionTimeout => 60000;

    private string _serverName = string.Empty;

    /// <summary>协商到的服务端名称，连接后才有值。</summary>
    public string ServerName => _serverName;

    public void Open()
    {
        if (string.IsNullOrWhiteSpace(_settings.EndpointUrl))
        {
            throw new ArgumentException("请填写 OPC UA 端点地址，如 opc.tcp://127.0.0.1:4840。");
        }

        if (!Uri.TryCreate(_settings.EndpointUrl.Trim(), UriKind.Absolute, out Uri? uri) || uri.Scheme != "opc.tcp")
        {
            throw new ArgumentException("端点地址应以 opc.tcp:// 开头。");
        }

        // OPC UA 栈内部有同步等待，在 UI 线程直接调用会死锁，统一放后台线程
        Opc.Ua.Client.Session session;
        try
        {
            session = Task.Run(ConnectCore).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"连接 {_settings.EndpointUrl} 失败：{DescribeConnectFailure(ex)}", ex);
        }

        lock (_syncRoot)
        {
            _session = session;
        }

        Interlocked.Exchange(ref _totalReceived, 0);
        Interlocked.Exchange(ref _totalSent, 0);

        ConnectionStateChanged?.Invoke(true);
    }

    /// <summary>把常见的连接失败翻译成可操作的提示。</summary>
    private static string DescribeConnectFailure(Exception ex)
    {
        string message = ex.Message;

        if (message.Contains("BadSecureChannelClosed") ||
            message.Contains("BadSecurityChecksFailed") ||
            message.Contains("SecurityChecksFailed"))
        {
            return message +
                "\n端点在建立会话时被关闭。常见原因：\n" +
                "1）该地址是发现服务（LDS）而非可建会话的 OPC UA 服务器，4840 端口常被 LDS 占用；\n" +
                "2）服务器的安全策略与客户端不匹配；\n" +
                "3）连接的是别的软件、且它拒绝了本客户端。";
        }

        if (message.Contains("BadConnectionRejected") || message.Contains("拒绝") ||
            message.Contains("无法连接") || message.Contains("actively refused"))
        {
            return message + "\n目标端口上没有可用的 OPC UA 服务端，请确认服务端已启动、地址与端口正确。";
        }

        return message;
    }

    /// <summary>真正建链的部分，跑在后台线程上。</summary>
    private Opc.Ua.Client.Session ConnectCore()
    {
        ApplicationConfiguration config = OpcUaConfig.CreateClient("WPF-UartAssist-Client");

        EndpointDescription description = Opc.Ua.Client.CoreClientUtils.SelectEndpoint(
            config, _settings.EndpointUrl.Trim(), _settings.UseSecurity, DiscoveryTimeoutMs);

        var endpoint = new ConfiguredEndpoint(null, description, EndpointConfiguration.Create(config));

        IUserIdentity identity = _settings.UseSecurity
            ? new UserIdentity(_settings.UserName ?? string.Empty, _settings.Password ?? string.Empty)
            : new UserIdentity(new AnonymousIdentityToken());

        Opc.Ua.Client.Session session = Opc.Ua.Client.Session.Create(
            config,
            endpoint,
            false,
            $"WPF-UartAssist-{Environment.ProcessId}",
            SessionTimeout,
            identity,
            null).GetAwaiter().GetResult();

        _serverName = description.Server?.ApplicationName?.Text ?? string.Empty;
        return session;
    }

    /// <summary>读取一个节点的值。</summary>
    public DataValue ReadNode(string nodeId)
    {
        Opc.Ua.Client.Session session = EnsureSession();

        var toRead = new ReadValueIdCollection
        {
            new() { NodeId = ParseNodeId(nodeId), AttributeId = Attributes.Value },
        };

        (DataValueCollection results, DiagnosticInfoCollection diagnostics) = Task.Run(() =>
        {
            session.Read(
                null,
                0,
                TimestampsToReturn.Both,
                toRead,
                out DataValueCollection values,
                out DiagnosticInfoCollection infos);

            return (values, infos);
        }).GetAwaiter().GetResult();

        _ = diagnostics;

        if (results.Count == 0)
        {
            throw new InvalidOperationException("服务端没有返回数据。");
        }

        DataValue value = results[0];

        if (StatusCode.IsBad(value.StatusCode))
        {
            throw new ServiceResultException(value.StatusCode);
        }

        long total = Interlocked.Add(ref _totalReceived, EstimateSize(value.Value));
        ReceivedCountChanged?.Invoke(total);
        return value;
    }

    /// <summary>写一个节点的值。value 需与节点数据类型匹配。</summary>
    public StatusCode WriteNode(string nodeId, object value)
    {
        Opc.Ua.Client.Session session = EnsureSession();

        var toWrite = new WriteValueCollection
        {
            new()
            {
                NodeId = ParseNodeId(nodeId),
                AttributeId = Attributes.Value,
                Value = new DataValue(new Variant(value)),
            },
        };

        (StatusCodeCollection results, DiagnosticInfoCollection diagnostics) = Task.Run(() =>
        {
            session.Write(
                null,
                toWrite,
                out StatusCodeCollection codes,
                out DiagnosticInfoCollection infos);

            return (codes, infos);
        }).GetAwaiter().GetResult();

        _ = diagnostics;

        if (results.Count == 0)
        {
            throw new InvalidOperationException("服务端没有返回写入结果。");
        }

        long total = Interlocked.Add(ref _totalSent, EstimateSize(value));
        SentCountChanged?.Invoke(total);
        return results[0];
    }

    /// <summary>浏览 Objects 文件夹下的直接子节点，返回 “NodeId  显示名” 列表。</summary>
    public IReadOnlyList<string> BrowseObjects()
    {
        Opc.Ua.Client.Session session = EnsureSession();

        var toBrowse = new BrowseDescriptionCollection
        {
            new()
            {
                NodeId = ObjectIds.ObjectsFolder,
                BrowseDirection = BrowseDirection.Forward,
                ReferenceTypeId = ReferenceTypeIds.Organizes,
                IncludeSubtypes = true,
                NodeClassMask = 0,
                ResultMask = (uint)BrowseResultMask.All,
            },
        };

        (BrowseResultCollection results, DiagnosticInfoCollection diagnostics) = Task.Run(() =>
        {
            session.Browse(
                null,
                null,
                0,
                toBrowse,
                out BrowseResultCollection urls,
                out DiagnosticInfoCollection infos);

            return (urls, infos);
        }).GetAwaiter().GetResult();

        _ = diagnostics;

        if (results.Count == 0 || results[0].References is null)
        {
            return [];
        }

        return results[0].References
            .Select(r => $"{r.NodeId}  {r.DisplayName}")
            .ToList();
    }

    private Opc.Ua.Client.Session EnsureSession()
    {
        Opc.Ua.Client.Session? session;

        lock (_syncRoot)
        {
            session = _session;
        }

        if (session is null || !session.Connected)
        {
            throw new InvalidOperationException("OPC UA 未连接，请先连接。");
        }

        return session;
    }

    private static NodeId ParseNodeId(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("NodeId 不能为空，例如 ns=2;s=Demo.Text。");
        }

        try
        {
            return NodeId.Parse(text.Trim());
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"NodeId 格式不正确：{text}", ex);
        }
    }

    private static long EstimateSize(object? value) => value switch
    {
        null => 0,
        byte[] bytes => bytes.Length,
        string text => System.Text.Encoding.UTF8.GetByteCount(text),
        bool => 1,
        sbyte or byte => 1,
        short or ushort => 2,
        int or uint or float => 4,
        long or ulong or double => 8,
        _ => System.Text.Encoding.UTF8.GetByteCount(Convert.ToString(value) ?? string.Empty),
    };

    /// <summary>OPC UA 是服务模型，没有“发字节”的概念。</summary>
    public int Send(byte[] data)
        => throw new NotSupportedException("OPC UA 请在节点面板里读取/写入，不能用字节发送。");

    public void Close()
    {
        Opc.Ua.Client.Session? session;

        lock (_syncRoot)
        {
            session = _session;
            _session = null;
        }

        if (session is null)
        {
            return;
        }

        try
        {
            Task.Run(() => session.Close()).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // 主动关闭不提示
        }

        session.Dispose();
    }

    public void Dispose()
    {
        Close();
        GC.SuppressFinalize(this);
    }
}
