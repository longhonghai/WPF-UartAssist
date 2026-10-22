using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

namespace WPF_UartAssist.Services;

/// <summary>服务端地址空间里的一个可读写变量，供界面展示与修改。</summary>
public sealed class OpcUaVariable
{
    internal OpcUaVariable(BaseDataVariableState node, string dataTypeName)
    {
        Node = node;
        DataType = dataTypeName;
    }

    internal BaseDataVariableState Node { get; }

    /// <summary>变量名，如 Text / Counter。</summary>
    public string Name => Node.SymbolicName;

    /// <summary>完整 NodeId，如 ns=2;s=Demo.Text。</summary>
    public string NodeId => Node.NodeId.ToString();

    /// <summary>数据类型，如 String / Int32。</summary>
    public string DataType { get; }

    /// <summary>当前值（字符串形式）。</summary>
    public string Value => FormatValue(Node.Value);

    /// <summary>本地改值，供界面模拟数据变化。</summary>
    public void SetValue(object value)
    {
        Node.Value = value;
        Node.Timestamp = DateTime.UtcNow;
        Node.ClearChangeMasks(null, false);
    }

    public static string FormatValue(object? value) => value switch
    {
        null => "(空)",
        bool b => b ? "true" : "false",
        double d => d.ToString("0.###"),
        float f => f.ToString("0.###"),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss"),
        _ => Convert.ToString(value) ?? string.Empty,
    };
}

/// <summary>
/// 内置的 OPC UA 服务端。监听 opc.tcp://&lt;本机&gt;:&lt;端口&gt;，
/// 提供 Demo 文件夹下的一组演示变量，安全策略 None + 匿名登录。
/// </summary>
public sealed class OpcUaServerHost : IDisposable
{
    private ApplicationInstance? _application;
    private DemosServer? _server;
    private DemoNodeManager? _nodeManager;

    /// <summary>监听端口。</summary>
    public int Port { get; private set; }

    /// <summary>客户端应连接的本机端点。</summary>
    public string EndpointUrl => $"opc.tcp://127.0.0.1:{Port}";

    public bool IsRunning => _application is not null && _server is not null;

    /// <summary>地址空间中的变量，服务端未启动时为空。</summary>
    public IReadOnlyList<OpcUaVariable> Variables => _nodeManager?.Variables ?? [];

    /// <summary>命名空间索引，随服务端启动确定。</summary>
    public ushort NamespaceIndex => _nodeManager?.NamespaceIndex ?? 2;

    /// <summary>Demo 节点前缀，例如 ns=2;s=Demo.</summary>
    public string DemoPrefix => $"ns={NamespaceIndex};s=Demo.";

    public void Start(int port)
    {
        if (IsRunning)
        {
            return;
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentException("端口应为 1–65535。");
        }

        Port = port;
        string url = EndpointUrl;

        ApplicationConfiguration config = OpcUaConfig.CreateServer("WPF-UartAssist-Server", url);
        var application = new ApplicationInstance
        {
            ApplicationName = "WPF-UartAssist-Server",
            ApplicationType = ApplicationType.Server,
            ApplicationConfiguration = config,
        };

        var server = new DemosServer();

        // OPC UA 栈内部有同步等待，在 UI 线程直接调用会死锁，统一放后台线程
        Task.Run(() =>
        {
            // 首次运行会生成自签名证书，约需 1 秒
            application.CheckApplicationInstanceCertificate(true, 0).GetAwaiter().GetResult();
            application.Start(server);
        }).GetAwaiter().GetResult();

        _application = application;
        _server = server;
        _nodeManager = server.NodeManager;
    }

    public void Stop()
    {
        try
        {
            ApplicationInstance? application = _application;

            if (application is not null)
            {
                Task.Run(() => application.Stop()).GetAwaiter().GetResult();
            }
        }
        catch (Exception)
        {
            // 停止失败不影响后续重启
        }

        _server = null;
        _application = null;
        _nodeManager = null;
    }

    /// <summary>按名称取变量，用于界面直接改值。</summary>
    public OpcUaVariable? Find(string name)
        => Variables.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));

    public void Dispose() => Stop();
}

/// <summary>把节点管理器暴露给外层的 StandardServer。</summary>
internal sealed class DemosServer : StandardServer
{
    public DemoNodeManager? NodeManager { get; private set; }

    protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
    {
        NodeManager = new DemoNodeManager(server, configuration);
        return new MasterNodeManager(server, configuration, null, NodeManager);
    }

    protected override ServerProperties LoadServerProperties()
        => new()
        {
            ManufacturerName = "WPF-UartAssist",
            ProductName = "WPF-UartAssist OPC UA Server",
            ProductUri = "http://example.org/WPF-UartAssist",
            SoftwareVersion = "1.0",
            BuildNumber = "1",
            BuildDate = DateTime.UtcNow,
        };
}

/// <summary>演示地址空间：Objects/Demo 下挂 Text / Counter / Enabled / Temperature 四个变量。</summary>
internal sealed class DemoNodeManager : CustomNodeManager2
{
    private const string DemoNamespace = "http://example.org/WPF-UartAssist/Demo";

    private readonly List<OpcUaVariable> _variables = [];

    public DemoNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        : base(server, configuration, DemoNamespace)
    {
        SystemContext.NodeIdFactory = this;
    }

    public IReadOnlyList<OpcUaVariable> Variables => _variables;

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out IList<IReference>? references))
            {
                externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();
            }

            FolderState root = CreateFolder(null, "Demo");
            root.AddReference(ReferenceTypes.Organizes, true, ObjectIds.ObjectsFolder);
            references.Add(new NodeStateReference(ReferenceTypes.Organizes, false, root.NodeId));
            root.EventNotifier = EventNotifiers.SubscribeToEvents;
            AddRootNotifier(root);

            CreateVariable(root, "Text", DataTypeIds.String, "String", "hello opc ua");
            CreateVariable(root, "Counter", DataTypeIds.Int32, "Int32", 0);
            CreateVariable(root, "Enabled", DataTypeIds.Boolean, "Boolean", true);
            CreateVariable(root, "Temperature", DataTypeIds.Double, "Double", 25.5);

            AddPredefinedNode(SystemContext, root);
        }
    }

    private FolderState CreateFolder(NodeState? parent, string name)
    {
        var folder = new FolderState(parent)
        {
            SymbolicName = name,
            ReferenceTypeId = ReferenceTypes.Organizes,
            TypeDefinitionId = ObjectTypeIds.FolderType,
            NodeId = new NodeId("Demo", NamespaceIndex),
            BrowseName = new QualifiedName("Demo", NamespaceIndex),
            DisplayName = new LocalizedText("Demo"),
            WriteMask = AttributeWriteMask.None,
            UserWriteMask = AttributeWriteMask.None,
            EventNotifier = EventNotifiers.None,
        };

        parent?.AddChild(folder);
        return folder;
    }

    private void CreateVariable(NodeState parent, string name, NodeId dataType, string typeName, object value)
    {
        var variable = new BaseDataVariableState(parent)
        {
            SymbolicName = name,
            ReferenceTypeId = ReferenceTypes.Organizes,
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            NodeId = new NodeId("Demo." + name, NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = new LocalizedText(name),
            DataType = dataType,
            ValueRank = ValueRanks.Scalar,
            AccessLevel = AccessLevels.CurrentReadOrWrite,
            UserAccessLevel = AccessLevels.CurrentReadOrWrite,
            Historizing = false,
            Value = value,
            StatusCode = StatusCodes.Good,
            Timestamp = DateTime.UtcNow,
        };

        parent.AddChild(variable);
        _variables.Add(new OpcUaVariable(variable, typeName));
    }
}
