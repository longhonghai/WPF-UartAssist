using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using WPF_UartAssist.Models;
using WPF_UartAssist.Mvvm;
using WPF_UartAssist.Services;

namespace WPF_UartAssist.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;

    /// <summary>串口连接（与网络、S7 连接可同时存在）。</summary>
    private ITransport? _serialTransport;

    /// <summary>TCP/UDP 连接（与串口、S7 连接可同时存在）。</summary>
    private ITransport? _networkTransport;

    /// <summary>S7 连接（与串口、网络连接可同时存在）。</summary>
    private ITransport? _s7Transport;

    /// <summary>当前页签：0=串口，1=TCP/UDP，2=S7。发送与状态栏跟随页签。</summary>
    private int _selectedTabIndex;
    private readonly DispatcherTimer _rxFlushTimer;
    private readonly DispatcherTimer _loopSendTimer;
    private readonly List<(string Tag, byte[] Data)> _pendingRx = [];
    private readonly object _pendingLock = new();
    private readonly Stopwatch _uptime = new();
    private readonly DispatcherTimer _memoryTimer;
    private Process? _currentProcess;
    private PerformanceCounter? _privateWsCounter;
    private bool _privateWsProbeStarted;
    private bool _privateWsProbeDone;
    private bool _disposed;

    private static readonly int[] DefaultBaudRates =
    [
        1200, 2400, 4800, 9600, 19200, 38400,
        57600, 115200, 230400, 460800, 921600, 1000000, 1152000, 2000000, 3000000
    ];

    private string _statusText = "未连接";
    private string _sendText = string.Empty;
    private string _sendHexText = string.Empty;
    private long _rxBytes;
    private long _txBytes;
    private double _rxRate;

    /// <summary>各通道（串口/网络/S7）的日志行数，状态栏显示当前页签的。</summary>
    private readonly int[] _rxLinesPerChannel = new int[3];

    public MainViewModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _settings = SettingsService.Load();
        Network = _settings.Network.Clone();
        S7SettingsModel = _settings.S7.Clone();

        // 内存占用显示：先占位为 "--"，后台探测专用工作集计数器后再显示，
        // 避免先显示完整工作集（数值偏大）再跳变
        _memoryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _memoryTimer.Tick += (_, _) => UpdateMemoryUsage();
        _memoryTimer.Start();
        StartPrivateWorkingSetProbe();

        RefreshPorts();
        BaudRates.Clear();
        foreach (int rate in DefaultBaudRates)
        {
            BaudRates.Add(rate);
        }

        // 上次使用的自定义波特率不在常用列表时，补进下拉
        if (!BaudRates.Contains(_settings.Serial.BaudRate))
        {
            BaudRates.Add(_settings.Serial.BaudRate);
        }

        Serial = _settings.Serial.Clone();
        Serial.Encoding = SelectedEncoding.Create();

        OpenCommand = new RelayCommand(OpenPort, () => _serialTransport?.IsOpen != true);
        CloseCommand = new RelayCommand(CloseSerial, () => _serialTransport?.IsOpen == true);
        OpenNetworkCommand = new RelayCommand(OpenNetwork, () => _networkTransport?.IsOpen != true);
        CloseNetworkCommand = new RelayCommand(CloseNetwork, () => _networkTransport?.IsOpen == true);
        OpenS7Command = new RelayCommand(OpenS7, () => _s7Transport?.IsOpen != true);
        CloseS7Command = new RelayCommand(CloseS7, () => _s7Transport?.IsOpen == true);
        RefreshPortsCommand = new RelayCommand(RefreshPorts);
        SendCommand = new RelayCommand(Send, () => IsOpen);
        SendFileCommand = new RelayCommand(SendFile, () => IsOpen);
        ClearLogCommand = new RelayCommand(ClearLog);
        SaveLogCommand = new RelayCommand(SaveLog);
        LoadSendFileCommand = new RelayCommand(LoadSendFile);
        ClearStatsCommand = new RelayCommand(ClearStats);
        ToggleLoopSendCommand = new RelayCommand(ToggleLoopSend);
        StopLoopSendCommand = new RelayCommand(StopLoopSend);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);

        Modbus = new ModbusRtuViewModel(payload => SendBytes(payload));

        // Modbus 报文是二进制帧：勾选构造器时切到 HEX 收发，取消时一并还原，
        // 避免文本模式下接收显示乱码/空白
        Modbus.VisibilityChanged += () =>
        {
            ReceiveHexMode = Modbus.IsVisible;
            SendHexMode = Modbus.IsVisible;
        };

        S7 = new S7ViewModel(payload => SendBytes(payload));

        // S7 报文是二进制帧：勾选构造器时切到 HEX 收发
        S7.VisibilityChanged += () =>
        {
            ReceiveHexMode = S7.IsVisible;
            SendHexMode = S7.IsVisible;
        };

        _rxFlushTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _rxFlushTimer.Tick += (_, _) => FlushReceiveBuffer();
        _rxFlushTimer.Start();

        _loopSendTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher);
        _loopSendTimer.Tick += (_, _) => OnLoopSendTick();

        _uptime.Start();
    }

    #region 串口参数

    public ObservableCollection<string> Ports { get; } = [];

    public ObservableCollection<int> BaudRates { get; } = [];

    public IReadOnlyList<int> DataBitsList { get; } = [5, 6, 7, 8];

    public IReadOnlyList<Parity> ParityList { get; } =
    [
        Parity.None, Parity.Odd, Parity.Even, Parity.Mark, Parity.Space
    ];

    public IReadOnlyList<StopBits> StopBitsList { get; } =
    [
        StopBits.One, StopBits.OnePointFive, StopBits.Two
    ];

    public IReadOnlyList<Handshake> HandshakeList { get; } =
    [
        Handshake.None, Handshake.XOnXOff, Handshake.RequestToSend, Handshake.RequestToSendXOnXOff
    ];

    public IReadOnlyList<TextEncodingOption> EncodingOptions { get; } = TextEncodingOption.All;

    private SerialPortSettings _serial = new();

    public SerialPortSettings Serial
    {
        get => _serial;
        set
        {
            if (SetProperty(ref _serial, value))
            {
                OnPropertyChanged(nameof(ConnectionSummary));
                OnPropertyChanged(nameof(IsOpen));
            }
        }
    }

    public string ConnectionSummary => IsOpen
        ? ActiveTransport?.Description ?? "已连接"
        : "未连接";

    #endregion

    #region 网络通讯（TCP / UDP）

    public NetworkSettings Network { get; }

    public IReadOnlyList<string> NetworkModes { get; } =
        ["TCP 客户端", "TCP 服务端", "UDP"];

    public string SelectedNetworkMode
    {
        get => Network.Mode switch
        {
            NetworkMode.TcpServer => "TCP 服务端",
            NetworkMode.Udp => "UDP",
            _ => "TCP 客户端"
        };
        set
        {
            NetworkMode mode = value switch
            {
                "TCP 服务端" => NetworkMode.TcpServer,
                "UDP" => NetworkMode.Udp,
                _ => NetworkMode.TcpClient
            };

            if (Network.Mode == mode)
            {
                return;
            }

            Network.Mode = mode;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NeedsRemoteHost));
            OnPropertyChanged(nameof(NeedsLocalPort));
            OnPropertyChanged(nameof(NetworkPortHint));
        }
    }

    /// <summary>TCP 客户端与 UDP 需要远端地址。</summary>
    public bool NeedsRemoteHost => Network.Mode != NetworkMode.TcpServer;

    /// <summary>服务端与 UDP 需要本地端口。</summary>
    public bool NeedsLocalPort => Network.Mode != NetworkMode.TcpClient;

    /// <summary>端口输入框的提示语随模式变化。</summary>
    public string NetworkPortHint => Network.Mode switch
    {
        NetworkMode.TcpServer => "本机监听端口，客户端连这个端口",
        NetworkMode.Udp => "本地端口填 0 表示由系统随机分配",
        _ => "远端服务端口"
    };

    #endregion

    #region S7 通讯（西门子 ISO-on-TCP）

    /// <summary>S7 连接参数。ISO-on-TCP 固定端口 102，机架与槽位决定 COTP 目标 TSAP。</summary>
    public Models.S7Settings S7SettingsModel { get; }

    /// <summary>协商到的 PDU 长度，仅 S7 连接建立后有值。</summary>
    public string S7PduSizeDisplay =>
        _s7Transport is S7Transport t && t.IsOpen ? $"{t.NegotiatedPduSize} 字节" : "--";

    #endregion

    #region 选中项代理属性

    public string SelectedPort
    {
        get => Serial.PortName;
        set => Serial.PortName = value;
    }

    public int SelectedBaudRate
    {
        get => Serial.BaudRate;
        set => Serial.BaudRate = value;
    }

    public int SelectedDataBits
    {
        get => Serial.DataBits;
        set => Serial.DataBits = value;
    }

    public Parity SelectedParity
    {
        get => Serial.Parity;
        set => Serial.Parity = value;
    }

    public StopBits SelectedStopBits
    {
        get => Serial.StopBits;
        set => Serial.StopBits = value;
    }

    public Handshake SelectedHandshake
    {
        get => Serial.Handshake;
        set => Serial.Handshake = value;
    }

    public TextEncodingOption SelectedEncoding
    {
        get => TextEncodingOption.Resolve(_settings.EncodingName);
        set
        {
            if (value is null)
            {
                return;
            }

            _settings.EncodingName = value.Name;
            Serial.Encoding = value.Create();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowEncodingWarning));
            OnPropertyChanged(nameof(EncodingWarningText));
        }
    }

    #endregion

    #region 状态

    /// <summary>当前页签对应的连接是否已打开。串口、网络、S7 可各自独立连接。</summary>
    public bool IsOpen => ActiveTransport?.IsOpen ?? false;

    /// <summary>当前页签对应的传输。</summary>
    private ITransport? ActiveTransport => _selectedTabIndex switch
    {
        1 => _networkTransport,
        2 => _s7Transport,
        _ => _serialTransport
    };

    public bool IsSerialTab => _selectedTabIndex == 0;

    public bool IsNetworkTab => _selectedTabIndex == 1;

    public bool IsS7Tab => _selectedTabIndex == 2;

    /// <summary>页签索引（0=串口，1=TCP/UDP，2=S7），切换时联动状态栏与协议构造器。</summary>
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (!SetProperty(ref _selectedTabIndex, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsSerialTab));
            OnPropertyChanged(nameof(IsNetworkTab));
            OnPropertyChanged(nameof(IsS7Tab));

            // 日志/行数/占位提示都跟随页签切换到对应通道
            OnPropertyChanged(nameof(RxLineCount));
            OnPropertyChanged(nameof(HasLogContent));

            // Modbus-RTU 是串口帧格式，网络与 S7 页签下不用它：切过去时自动收起
            if (!IsSerialTab && Modbus.IsVisible)
            {
                Modbus.IsVisible = false;
            }

            // S7 构造器只在 S7 页签下有意义
            if (!IsS7Tab && S7.IsVisible)
            {
                S7.IsVisible = false;
            }

            RefreshConnectionUi();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>状态指示灯颜色键。</summary>
    public string StatusBrushKey => IsOpen ? "OnlineBrush" : "OfflineBrush";

    /// <summary>按当前页签刷新状态栏、统计与命令可用性。</summary>
    private void RefreshConnectionUi()
    {
        ITransport? transport = ActiveTransport;
        bool open = transport?.IsOpen ?? false;

        StatusText = open ? $"已连接 {transport!.Description}" : "未连接";

        RxBytes = transport?.TotalReceived ?? 0;
        TxBytes = transport?.TotalSent ?? 0;

        OnPropertyChanged(nameof(IsOpen));
        OnPropertyChanged(nameof(ConnectionSummary));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrushKey));
        RefreshCommands();

        if (!open)
        {
            StopLoopSend();
        }
    }

    public long RxBytes
    {
        get => _rxBytes;
        private set
        {
            if (SetProperty(ref _rxBytes, value))
            {
                OnPropertyChanged(nameof(RxDisplay));
            }
        }
    }

    public long TxBytes
    {
        get => _txBytes;
        private set
        {
            if (SetProperty(ref _txBytes, value))
            {
                OnPropertyChanged(nameof(TxDisplay));
            }
        }
    }

    public string RxDisplay => FormatBytes(_rxBytes);

    public string TxDisplay => FormatBytes(_txBytes);

    public double RxRate
    {
        get => _rxRate;
        private set => SetProperty(ref _rxRate, value);
    }

    public string RxRateDisplay => $"{_rxRate:0.0} KB/s";

    private string _memoryDisplay = "--";

    /// <summary>当前进程内存占用（工作集）。</summary>
    public string MemoryDisplay
    {
        get => _memoryDisplay;
        private set => SetProperty(ref _memoryDisplay, value);
    }

    /// <summary>刷新内存占用显示。定时器在 UI 线程触发，无并发问题。</summary>
    private void UpdateMemoryUsage()
    {
        try
        {
            _currentProcess ??= Process.GetCurrentProcess();
            _currentProcess.Refresh();

            // 优先用「专用工作集」（任务管理器内存列的口径）；
            // 探测还没完成就先不显示，探测失败则退回完整工作集
            double mb;

            if (TryGetPrivateWorkingSetMb(out double privateMb))
            {
                mb = privateMb;
            }
            else if (_privateWsProbeDone)
            {
                mb = _currentProcess.WorkingSet64 / 1024.0 / 1024.0;
            }
            else
            {
                return;
            }

            MemoryDisplay = $"{mb:0.0} MB";
        }
        catch
        {
            // 进程对象失效等异常不影响主流程，显示保持原值
        }
    }

    /// <summary>
    /// 读取本进程「专用工作集」（Working Set - Private），
    /// 与任务管理器详细信息页的内存列同口径。
    /// </summary>
    private bool TryGetPrivateWorkingSetMb(out double mb)
    {
        mb = 0;

        // 后台探测尚未完成时直接返回，不要在这里同步枚举计数器（很慢）
        PerformanceCounter? counter = _privateWsCounter;
        if (counter is null)
        {
            return false;
        }

        try
        {
            mb = counter.NextValue() / 1024.0 / 1024.0;
            return mb > 0;
        }
        catch
        {
            // 计数器不可用（权限/实例名变化），退回完整工作集口径
            _privateWsCounter?.Dispose();
            _privateWsCounter = null;
            return false;
        }
    }

    /// <summary>
    /// 后台线程探测「专用工作集」计数器，探测完成后自动切到这个口径。
    /// 首次探测要遍历系统全部进程的性能计数器实例，实测约 1 秒，
    /// 绝不能放在 UI 线程上做。
    /// </summary>
    private void StartPrivateWorkingSetProbe()
    {
        if (_privateWsProbeStarted)
        {
            return;
        }

        _privateWsProbeStarted = true;

        Task.Run(() =>
        {
            PerformanceCounter? counter = null;

            try
            {
                counter = FindPrivateWorkingSetCounter();
            }
            catch
            {
                // 拿不到就一直用完整工作集，不影响显示
            }

            _dispatcher.BeginInvoke(() =>
            {
                if (_disposed)
                {
                    counter?.Dispose();
                    return;
                }

                _privateWsCounter = counter;
                _privateWsProbeDone = true;
                UpdateMemoryUsage();
            });
        });
    }

    /// <summary>
    /// 定位本进程的性能计数器实例。同名进程多开时实例名带 #1/#2 后缀，
    /// 所以用「ID Process」计数器按 PID 精确匹配，而不是按名字猜。
    /// </summary>
    private static PerformanceCounter? FindPrivateWorkingSetCounter()
    {
        const string wsCounterName = "Working Set - Private";
        int pid = Environment.ProcessId;
        string processName = Process.GetCurrentProcess().ProcessName;

        var category = new PerformanceCounterCategory("Process");

        foreach (string instance in category.GetInstanceNames())
        {
            if (!instance.StartsWith(processName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                using var idCounter = new PerformanceCounter(
                    "Process", "ID Process", instance, readOnly: true);

                if ((int)idCounter.NextValue() != pid)
                {
                    continue;
                }

                return new PerformanceCounter(
                    "Process", wsCounterName, instance, readOnly: true);
            }
            catch (InvalidOperationException)
            {
                // 实例刚好被回收，跳过
            }
        }

        return null;
    }

    public int RxLineCount
    {
        get => _rxLinesPerChannel[(int)CurrentChannel];
        private set => _rxLinesPerChannel[(int)CurrentChannel] = value;
    }

    #endregion

    #region 日志与发送

    /// <summary>当前页签对应的日志通道（索引即通道枚举值）。</summary>
    private LogChannel CurrentChannel => (LogChannel)_selectedTabIndex;

    /// <summary>追加一条日志。prefix 为时间戳/字节数等前缀（弱化色），content 为正文（按类型着色）。</summary>
    public event Action<LogChannel, string, string, LogKind>? LogAppended;

    /// <summary>某个通道的日志被清空。</summary>
    public event Action<LogChannel>? LogCleared;

    /// <summary>各通道独立的纯文本行缓存（供导出与行数统计，有上限，防止长时间运行内存膨胀）。</summary>
    private readonly Queue<string>[] _logLines = [new(), new(), new()];

    public bool HasLogContent => _logLines[(int)CurrentChannel].Count > 0;

    /// <summary>日志最大保留行数。</summary>
    public int LogLineLimit => _settings.MaxLogLines;

    public string SendText
    {
        get => _sendText;
        set
        {
            if (SetProperty(ref _sendText, value))
            {
                OnPropertyChanged(nameof(IsSendHexMode));
                OnPropertyChanged(nameof(ShowEncodingWarning));
            }
        }
    }

    /// <summary>
    /// 当前编码无法表示输入中的部分字符时给出提示。
    /// 例如 Latin-1 下输入中文，发送时会变成 '?'，数据在发送端就已损坏。
    /// </summary>
    public bool ShowEncodingWarning
    {
        get
        {
            if (SendHexMode || string.IsNullOrEmpty(_sendText))
            {
                return false;
            }

            return IsLossyEncoding(Serial.Encoding, _sendText);
        }
    }

    public string EncodingWarningText =>
        $"{TextEncodingOption.Describe(Serial.Encoding)} 无法表示输入中的部分字符，发送时会变成 \"?\"。请改用 GBK 或 UTF-8。";

    /// <summary>
    /// 用异常回退编码试编一次，精确判断会不会丢字符。
    /// 只采样前 500 字符，避免长文本每次按键都全量编码。
    /// </summary>
    private static bool IsLossyEncoding(System.Text.Encoding encoding, string text)
    {
        string sample = text.Length > 500 ? text[..500] : text;

        // 纯 7-bit 内容任何编码都不会丢
        if (sample.All(c => c <= 0x7F))
        {
            return false;
        }

        try
        {
            var strict = (System.Text.Encoding)encoding.Clone();
            strict.EncoderFallback = System.Text.EncoderFallback.ExceptionFallback;
            strict.GetBytes(sample);
            return false;
        }
        catch (System.Text.EncoderFallbackException)
        {
            return true;
        }
    }

    public string SendHexText
    {
        get => _sendHexText;
        set
        {
            if (SetProperty(ref _sendHexText, value))
            {
                UpdateHexError();
            }
        }
    }

    /// <summary>HEX 输入变化时实时校验，非法时给出位置提示。</summary>
    private void UpdateHexError()
    {
        if (HexConverter.TryParse(_sendHexText, out _, out int errorIndex))
        {
            SendHexError = string.Empty;
        }
        else
        {
            SendHexError = $"第 {Math.Max(errorIndex, 0) + 1} 个字符处不是合法 HEX 数据";
        }
    }

    private string _sendHexError = string.Empty;

    public string SendHexError
    {
        get => _sendHexError;
        private set
        {
            if (SetProperty(ref _sendHexError, value))
            {
                OnPropertyChanged(nameof(HasSendHexError));
            }
        }
    }

    public bool HasSendHexError => !string.IsNullOrEmpty(_sendHexError);

    #endregion

    #region 显示选项

    public bool ReceiveHexMode
    {
        get => _settings.ReceiveHexMode;
        set
        {
            if (_settings.ReceiveHexMode != value)
            {
                _settings.ReceiveHexMode = value;
                OnPropertyChanged();
            }
        }
    }

    public bool SendHexMode
    {
        get => _settings.SendHexMode;
        set
        {
            if (_settings.SendHexMode != value)
            {
                _settings.SendHexMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsSendHexMode));
                OnPropertyChanged(nameof(ShowEncodingWarning));
            }
        }
    }

    public bool IsSendHexMode => SendHexMode;

    public bool ShowTimestamp
    {
        get => _settings.ShowTimestamp;
        set
        {
            if (_settings.ShowTimestamp != value)
            {
                _settings.ShowTimestamp = value;
                OnPropertyChanged();
            }
        }
    }

    public bool AppendNewLine
    {
        get => _settings.AppendNewLine;
        set
        {
            if (_settings.AppendNewLine != value)
            {
                _settings.AppendNewLine = value;
                OnPropertyChanged();
            }
        }
    }

    public bool AutoScroll
    {
        get => _settings.AutoScroll;
        set
        {
            if (_settings.AutoScroll != value)
            {
                _settings.AutoScroll = value;
                OnPropertyChanged();
            }
        }
    }

    public bool WordWrap
    {
        get => _settings.WordWrap;
        set
        {
            if (_settings.WordWrap != value)
            {
                _settings.WordWrap = value;
                OnPropertyChanged();
            }
        }
    }

    public bool AutoClearOnOpen
    {
        get => _settings.AutoClearOnOpen;
        set
        {
            if (_settings.AutoClearOnOpen != value)
            {
                _settings.AutoClearOnOpen = value;
                OnPropertyChanged();
            }
        }
    }

    public double LogFontSize
    {
        get => _settings.FontSize;
        set
        {
            if (Math.Abs(_settings.FontSize - value) > 0.01)
            {
                _settings.FontSize = value;
                OnPropertyChanged();
            }
        }
    }

    public string ReceiveFilter
    {
        get => _settings.ReceiveFilter;
        set
        {
            if (_settings.ReceiveFilter != value)
            {
                _settings.ReceiveFilter = value;
                OnPropertyChanged();
            }
        }
    }

    public LoopSendConfig LoopConfig { get; } = new();

    public bool IsLoopSending => _loopSendTimer.IsEnabled;

    public string LoopButtonText => IsLoopSending ? "停止循环" : "循环发送";

    #endregion

    #region 命令

    public RelayCommand OpenCommand { get; }
    public RelayCommand CloseCommand { get; }
    public RelayCommand RefreshPortsCommand { get; }
    public RelayCommand SendCommand { get; }
    public RelayCommand SendFileCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand SaveLogCommand { get; }
    public RelayCommand LoadSendFileCommand { get; }
    public RelayCommand ClearStatsCommand { get; }
    public RelayCommand ToggleLoopSendCommand { get; }
    public RelayCommand StopLoopSendCommand { get; }
    public RelayCommand ToggleThemeCommand { get; }

    public RelayCommand OpenNetworkCommand { get; }
    public RelayCommand CloseNetworkCommand { get; }

    public RelayCommand OpenS7Command { get; }
    public RelayCommand CloseS7Command { get; }

    #endregion

    #region 主题

    /// <summary>主题切换后触发，供界面重新着色已有日志行。</summary>
    public event Action? ThemeChanged;

    /// <summary>按钮字形用 Segoe MDL2 Assets：暗色显示月亮，亮色显示太阳。</summary>
    public string ThemeGlyph => ThemeManager.IsDark ? "\uE708" : "\uE706";

    public string ThemeToolTip => ThemeManager.IsDark ? "切换到亮色主题" : "切换到暗色主题";

    private void ToggleTheme()
    {
        string next = ThemeManager.Toggle(ThemeManager.Current);
        ThemeManager.Apply(next);
        _settings.Theme = next;

        OnPropertyChanged(nameof(ThemeGlyph));
        OnPropertyChanged(nameof(ThemeToolTip));
        ThemeChanged?.Invoke();
    }

    #endregion

    #region 串口控制

    private void ToggleOpen()
    {
        if (IsOpen)
        {
            CloseActiveTab();
        }
        else
        {
            OpenActiveTab();
        }
    }

    /// <summary>按当前页签打开对应连接。</summary>
    private void OpenActiveTab()
    {
        switch (_selectedTabIndex)
        {
            case 1: OpenNetwork(); break;
            case 2: OpenS7(); break;
            default: OpenPort(); break;
        }
    }

    /// <summary>按当前页签关闭对应连接。</summary>
    private void CloseActiveTab()
    {
        switch (_selectedTabIndex)
        {
            case 1: CloseNetwork(); break;
            case 2: CloseS7(); break;
            default: CloseSerial(); break;
        }
    }

    private void OpenPort()
    {
        if (_serialTransport?.IsOpen == true)
        {
            ShowWarning("串口已打开", "串口连接已存在，请先关闭。");
            return;
        }

        var settings = Serial.Clone();
        settings.Encoding = SelectedEncoding.Create();

        string? error = SerialPortService.Validate(settings);
        if (error is not null)
        {
            ShowWarning("参数无效", error);
            return;
        }

        var transport = new SerialPortService(settings);

        if (OpenTransport(transport, slot: 0, $"已打开 {SerialPortService.Describe(settings)}"))
        {
            SavePortToHistory(settings.PortName);
        }
    }

    /// <summary>按当前模式打开 TCP/UDP 连接。</summary>
    private void OpenNetwork()
    {
        if (_networkTransport?.IsOpen == true)
        {
            ShowWarning("已连接", "TCP/UDP 连接已存在，请先断开。");
            return;
        }

        if (!int.TryParse(Network.RemotePort?.Trim(), out int remotePort) || remotePort is < 1 or > 65535)
        {
            ShowWarning("参数无效", "远端端口应为 1–65535。");
            return;
        }

        if (!int.TryParse(Network.LocalPort?.Trim(), out int localPort) || localPort is < 0 or > 65535)
        {
            ShowWarning("参数无效", "本地端口应为 0–65535（0 表示随机）。");
            return;
        }

        string host = (Network.RemoteHost ?? string.Empty).Trim();

        if (NeedsRemoteHost && host.Length == 0)
        {
            ShowWarning("参数无效", "请填写远端地址（IP 或主机名）。");
            return;
        }

        if (Network.Mode == NetworkMode.TcpServer && localPort == 0)
        {
            ShowWarning("参数无效", "TCP 服务端必须指定监听端口。");
            return;
        }

        ITransport transport;

        try
        {
            transport = Network.Mode switch
            {
                NetworkMode.TcpServer => new TcpServerTransport(localPort),
                NetworkMode.Udp => new UdpTransport(localPort, host, remotePort),
                _ => new TcpClientTransport(host, remotePort)
            };
        }
        catch (Exception ex)
        {
            ShowWarning("参数无效", ex.Message);
            return;
        }

        OpenTransport(transport, slot: 1, $"已打开 {transport.Description}");
    }

    /// <summary>打开 S7 连接：内部完成 TCP 连接、COTP 握手与 PDU 协商。</summary>
    private void OpenS7()
    {
        if (_s7Transport?.IsOpen == true)
        {
            ShowWarning("已连接", "S7 连接已存在，请先断开。");
            return;
        }

        if (string.IsNullOrWhiteSpace(S7SettingsModel.Host))
        {
            ShowWarning("参数无效", "请填写 PLC 地址。");
            return;
        }

        var transport = new S7Transport(S7SettingsModel);

        if (OpenTransport(transport, slot: 2, $"已打开 {transport.Description}"))
        {
            OnPropertyChanged(nameof(S7PduSizeDisplay));
        }
    }

    /// <summary>
    /// 打开一个传输，成功后登记为串口/网络/S7 连接。返回是否成功。
    /// 三条连接互不影响，可同时保持。
    /// </summary>
    private bool OpenTransport(ITransport transport, int slot, string logText)
    {
        try
        {
            AttachTransport(transport);
            transport.Open();
        }
        catch (Exception ex)
        {
            transport.Dispose();
            ShowWarning("连接失败", ex.Message);
            RefreshConnectionUi();
            return false;
        }

        switch (slot)
        {
            case 1:
                _networkTransport = transport;
                break;
            case 2:
                _s7Transport = transport;
                break;
            default:
                _serialTransport = transport;
                break;
        }

        if (_settings.AutoClearOnOpen)
        {
            ClearChannelLog(ChannelFromSlot(slot));
        }

        AppendSystem(ChannelFromSlot(slot), $"[{NowStamp()}] {logText}");
        RefreshConnectionUi();
        return true;
    }

    /// <summary>打开/关闭传输时的槽位 → 日志通道。</summary>
    private static LogChannel ChannelFromSlot(int slot) => slot switch
    {
        1 => LogChannel.Network,
        2 => LogChannel.S7,
        _ => LogChannel.Serial
    };

    private void CloseSerial() => CloseTransport(_serialTransport);

    private void CloseNetwork() => CloseTransport(_networkTransport);

    private void CloseS7() => CloseTransport(_s7Transport);

    private void CloseTransport(ITransport? transport)
    {
        if (transport is null)
        {
            return;
        }

        string description = transport.Description;

        try
        {
            transport.Close();
        }
        catch (Exception ex)
        {
            ShowWarning("关闭连接失败", ex.Message);
        }
        finally
        {
            // 引用会被置空，先确定通道
            LogChannel channel = ChannelOf(transport);

            transport.Dispose();

            if (ReferenceEquals(transport, _serialTransport))
            {
                _serialTransport = null;
            }

            if (ReferenceEquals(transport, _networkTransport))
            {
                _networkTransport = null;
            }

            if (ReferenceEquals(transport, _s7Transport))
            {
                _s7Transport = null;
                OnPropertyChanged(nameof(S7PduSizeDisplay));
            }

            AppendSystem(channel, $"[{NowStamp()}] 已关闭 {description}");
            RefreshConnectionUi();
        }
    }

    /// <summary>按当前登记的引用判断传输属于哪个日志通道（关闭/断开时引用还在）。</summary>
    private LogChannel ChannelOf(ITransport transport)
    {
        if (ReferenceEquals(transport, _networkTransport))
        {
            return LogChannel.Network;
        }

        if (ReferenceEquals(transport, _s7Transport))
        {
            return LogChannel.S7;
        }

        return LogChannel.Serial;
    }

    private void AttachTransport(ITransport transport)
    {
        // 用闭包带上来源，便于多路数据在同一日志里区分
        transport.DataReceived += data => OnDataReceived(transport.Tag, data);
        transport.ErrorOccurred += OnTransportError;
        transport.ConnectionStateChanged += connected => OnTransportStateChanged(transport, connected);
    }

    private void OnTransportError(Exception ex)
        => _dispatcher.BeginInvoke(() => ShowWarning("通讯异常", ex.Message));

    /// <summary>
    /// 对端断开、监听异常等外部原因导致的断线。
    /// 主动关闭不会触发该事件，所以这里一定是意外。
    /// </summary>
    private void OnTransportStateChanged(ITransport transport, bool connected)
    {
        if (connected)
        {
            return;
        }

        _dispatcher.BeginInvoke(() =>
        {
            // 引用会被置空，先确定通道
            LogChannel channel = ChannelOf(transport);

            if (ReferenceEquals(transport, _serialTransport))
            {
                _serialTransport = null;
            }

            if (ReferenceEquals(transport, _networkTransport))
            {
                _networkTransport = null;
            }

            if (ReferenceEquals(transport, _s7Transport))
            {
                _s7Transport = null;
                OnPropertyChanged(nameof(S7PduSizeDisplay));
            }

            transport.Dispose();
            AppendSystem(channel, $"[{NowStamp()}] {transport.Description} 已断开");
            RefreshConnectionUi();
        });
    }

    public void RefreshPorts()
    {
        string[] names = SerialPortService.GetPortNames();
        var current = Serial.PortName;

        Ports.Clear();
        foreach (string name in names)
        {
            Ports.Add(name);
        }

        if (Ports.Count == 0)
        {
            // 没有物理串口时保留一个可输入项，便于用户手动输入
            Ports.Add(current);
        }

        if (!string.IsNullOrEmpty(current) && !Ports.Contains(current) && names.Length > 0)
        {
            Ports.Add(current);
        }

        if (Ports.Count > 0)
        {
            Serial.PortName = Ports.Contains(current) ? current : Ports[0];
        }
    }

    private void SavePortToHistory(string portName)
    {
        if (!_settings.SavedPorts.Contains(portName))
        {
            _settings.SavedPorts.Add(portName);
            if (_settings.SavedPorts.Count > 20)
            {
                _settings.SavedPorts.RemoveAt(0);
            }
        }
    }

    #endregion

    #region 接收

    private void FlushReceiveBuffer()
    {
        List<(string Tag, byte[] Data)> batches;

        lock (_pendingLock)
        {
            if (_pendingRx.Count == 0)
            {
                return;
            }

            batches = _pendingRx.ToList();
            _pendingRx.Clear();
        }

        string? filter = string.IsNullOrWhiteSpace(ReceiveFilter) ? null : ReceiveFilter;

        // 同一来源的连续数据合并成一条日志；串口与网络交替时各成一块
        int index = 0;

        while (index < batches.Count)
        {
            string tag = batches[index].Tag;
            var merged = new List<byte>();

            while (index < batches.Count && batches[index].Tag == tag)
            {
                merged.AddRange(batches[index].Data);
                index++;
            }

            byte[] chunk = merged.ToArray();
            if (chunk.Length == 0)
            {
                continue;
            }

            // 串口沿用原来的显示格式，网络与 S7 数据显示来源标签
            string? label = tag == "串口" ? null : tag;

            if (ReceiveHexMode)
            {
                // 单行显示：不按 16 字节断行，超宽时靠横向滚动条查看
                string hexText = HexConverter.ToHex(chunk);
                if (filter is null || hexText.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    AppendLog(hexText, chunk.Length, label);
                }
            }
            else
            {
                string text = Serial.Encoding.GetString(chunk);
                if (filter is null || text.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    AppendLog(text, chunk.Length, label);
                }
            }

            // S7 是有结构的协议：HEX 行之外再补一条解码说明，方便直接读值
            if (tag == "S7")
            {
                string? decoded = DecodeS7Response(chunk);

                if (decoded is not null)
                {
                    AppendEntry(LogChannel.S7, ShowTimestamp ? $"[{NowStamp()}] " : string.Empty, decoded, LogKind.System);
                }
            }
        }

        RxBytes = ActiveTransport?.TotalReceived ?? 0;
        RxRate = RxBytes / Math.Max(0.001, _uptime.Elapsed.TotalSeconds) / 1024.0;
    }

    /// <summary>接收日志：每帧带上时间戳与本次收到的字节数，多路时带来源标签。</summary>
    private void AppendLog(string content, int byteCount, string? sourceTag = null)
    {
        if (string.IsNullOrEmpty(content))
        {
            return;
        }

        string prefix = ShowTimestamp ? $"[{NowStamp()}] " : string.Empty;
        prefix += sourceTag is null
            ? $"接收 {byteCount} 字节："
            : $"接收 {byteCount} 字节 [{sourceTag}]：";

        AppendEntry(ChannelFromTag(sourceTag), prefix, content, LogKind.Receive);
    }

    /// <summary>来源标签 → 日志通道：串口无标签，TCP/UDP 归网络，S7 归 S7。</summary>
    private static LogChannel ChannelFromTag(string? tag) => tag switch
    {
        null or "串口" => LogChannel.Serial,
        "S7" => LogChannel.S7,
        _ => LogChannel.Network
    };

    /// <summary>
    /// 解析 S7 应答，返回一句可读说明；格式不符时返回 null。
    /// 只能解出字节层面的信息，数值类型以构造器当前选择为准。
    /// </summary>
    private string? DecodeS7Response(byte[] pdu)
    {
        if (pdu.Length < 2 || pdu[0] != S7Comm.ProtocolId)
        {
            return null;
        }

        byte rosctr = pdu[1];
        int headerSize = S7Comm.GetHeaderSize(rosctr);

        if (pdu.Length < headerSize)
        {
            return null;
        }

        int paramLen = (pdu[6] << 8) | pdu[7];
        int dataLen = (pdu[8] << 8) | pdu[9];

        string type = rosctr switch
        {
            S7Comm.RosctrAck => "应答",
            S7Comm.RosctrAckData => "带数据应答",
            S7Comm.RosctrJob => "请求",
            _ => $"类型 0x{rosctr:X2}"
        };

        int dataOffset = headerSize + paramLen;

        // PDU 协商的应答：参数区里带回实际允许的 PDU 长度
        if (rosctr == S7Comm.RosctrAckData && paramLen >= 8 && pdu[headerSize] == S7Comm.FuncSetupCommunication)
        {
            int lenBytes = pdu[dataOffset - 2];

            if (lenBytes is >= 1 and <= 2 && pdu.Length >= dataOffset + lenBytes)
            {
                int size = 0;
                for (int i = 0; i < lenBytes; i++)
                {
                    size = (size << 8) | pdu[dataOffset + i];
                }

                return $"S7 通讯协商完成：PLC 允许的 PDU 长度 {size} 字节。";
            }
        }

        // 有错误类：直接说明错误
        if (rosctr is S7Comm.RosctrAck or S7Comm.RosctrAckData && pdu[10] != 0x00)
        {
            return $"S7 {type}：{S7Comm.DescribeErrorClass(pdu[10])}，错误码 0x{pdu[11]:X2}。";
        }

        // 读写应答：逐项解析数据区
        if (rosctr == S7Comm.RosctrAckData && paramLen >= 2 && pdu.Length > dataOffset)
        {
            byte function = pdu[headerSize];

            // 写应答的数据区只有一个返回码，不含数据项
            if (function == S7Comm.FuncWriteVar)
            {
                return S7Comm.IsWriteSuccess(pdu.AsSpan(dataOffset, dataLen))
                    ? "S7 写变量应答：写入成功。"
                    : $"S7 写变量应答：{S7Comm.DescribeReturnCode(pdu[dataOffset])}。";
            }

            if (function != S7Comm.FuncReadVar)
            {
                return $"S7 应答：功能码 0x{function:X2}，参数 {paramLen} 字节，数据 {dataLen} 字节。";
            }

            var items = S7Comm.ParseReadItems(pdu.AsSpan(dataOffset, dataLen));

            if (items.Count == 0)
            {
                return "S7 读变量应答：无数据。";
            }

            var parts = new List<string>();

            foreach (var (returnCode, transportSize, data) in items)
            {
                if (returnCode != S7Comm.ReturnCodeSuccess)
                {
                    parts.Add(S7Comm.DescribeReturnCode(returnCode));
                    continue;
                }

                parts.Add($"{HexConverter.ToHex(data)}");
            }

            string summary = $"S7 读变量应答：{string.Join("；", parts)}";

            // 数值按构造器选定的类型渲染，未选定时只给十六进制
            if (items[0].ReturnCode == S7Comm.ReturnCodeSuccess)
            {
                string rendered = RenderS7Value(items[0].Data);

                if (rendered.Length > 0)
                {
                    summary += $"（按 {S7.SelectedDataType} 解析：{rendered}）";
                }
            }

            return summary;
        }

        return $"S7 {type}：参数 {paramLen} 字节，数据 {dataLen} 字节。";
    }

    /// <summary>按构造器当前选定的数据类型渲染数值，避免猜错类型给出错误结论。</summary>
    private string RenderS7Value(byte[] data)
    {
        if (data.Length == 0)
        {
            return string.Empty;
        }

        S7Comm.DataType type = S7Comm.FindType(S7.SelectedDataType);

        if (type.Size == 0 || data.Length % type.Size != 0)
        {
            return string.Empty;
        }

        var parts = new List<string>();

        for (int offset = 0; offset + type.Size <= data.Length; offset += type.Size)
        {
            parts.Add(S7Comm.FormatValue(type, data[offset..(offset + type.Size)]));
        }

        return string.Join(", ", parts);
    }

    /// <summary>统一入口：入对应通道的缓存队列（供导出/统计）并通知视图着色追加。</summary>
    private void AppendEntry(LogChannel channel, string prefix, string content, LogKind kind)
    {
        string plain = prefix + content;
        Queue<string> lines = _logLines[(int)channel];
        lines.Enqueue(plain);
        while (lines.Count > _settings.MaxLogLines)
        {
            lines.Dequeue();
        }

        _rxLinesPerChannel[(int)channel] += plain.Count(c => c == '\n') + 1;

        LogAppended?.Invoke(channel, prefix, content, kind);

        // 只有当前页签的通道变化才影响界面显示
        if (channel == CurrentChannel)
        {
            OnPropertyChanged(nameof(RxLineCount));
            OnPropertyChanged(nameof(HasLogContent));
        }
    }

    /// <summary>时间戳格式：默认精确到毫秒（3 位小数）。</summary>
    private string TimestampFormat => _settings.TimestampWithMs ? "HH:mm:ss.fff" : "HH:mm:ss";

    /// <summary>当前时刻字符串，收发日志统一用它。</summary>
    private string NowStamp() => DateTime.Now.ToString(TimestampFormat);

    private void AppendSystem(LogChannel channel, string text) => AppendEntry(channel, string.Empty, text, LogKind.System);

    /// <summary>发送日志：格式与接收对齐，视图按 Send 类型着色为蓝色。</summary>
    private void AppendSend(string preview, int byteCount)
    {
        string prefix = ShowTimestamp ? $"[{NowStamp()}] " : string.Empty;
        prefix += $"发送 {byteCount} 字节：";
        AppendEntry(CurrentChannel, prefix, preview, LogKind.Send);
    }

    /// <summary>清空当前页签对应通道的日志。</summary>
    public void ClearLog()
    {
        ClearChannelLog(CurrentChannel);
    }

    /// <summary>清空指定通道的日志（打开连接时的“打开时清空”也走这里）。</summary>
    private void ClearChannelLog(LogChannel channel)
    {
        _logLines[(int)channel].Clear();
        _rxLinesPerChannel[(int)channel] = 0;
        LogCleared?.Invoke(channel);

        if (channel == CurrentChannel)
        {
            OnPropertyChanged(nameof(RxLineCount));
            OnPropertyChanged(nameof(HasLogContent));
        }
    }

    public void ClearStats()
    {
        _uptime.Restart();
        RxRate = 0;
    }

    public void SaveLog()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "文本文件 (*.txt)|*.txt|日志文件 (*.log)|*.log|所有文件 (*.*)|*.*",
            FileName = $"uart_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            InitialDirectory = Directory.Exists(_settings.LastLogDirectory)
                ? _settings.LastLogDirectory
                : null
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            // 只导出当前页签通道的日志
            File.WriteAllText(dialog.FileName, string.Join(Environment.NewLine, _logLines[(int)CurrentChannel]));
            _settings.LastLogDirectory = Path.GetDirectoryName(dialog.FileName);
            ShowInfo("导出成功", $"日志已保存到：\n{dialog.FileName}");
        }
        catch (Exception ex)
        {
            ShowWarning("导出失败", ex.Message);
        }
    }

    #endregion

    #region 发送

    private byte[] BuildSendPayload()
    {
        byte[] payload;

        if (SendHexMode)
        {
            if (!HexConverter.TryParse(SendHexText, out payload, out int errorIndex))
            {
                throw new InvalidOperationException($"第 {errorIndex + 1} 个字符处不是合法 HEX 数据。");
            }
        }
        else
        {
            payload = Serial.Encoding.GetBytes(SendText ?? string.Empty);
        }

        // S7 走 TCP 102 且报文带长度字段，多加一个换行会让 PLC 直接丢弃该帧
        bool appendNewLine = AppendNewLine && !IsS7Tab;

        if (appendNewLine && payload.Length > 0)
        {
            byte[] suffix = Serial.Encoding.GetBytes("\r\n");
            var combined = new byte[payload.Length + suffix.Length];
            Buffer.BlockCopy(payload, 0, combined, 0, payload.Length);
            Buffer.BlockCopy(suffix, 0, combined, payload.Length, suffix.Length);
            payload = combined;
        }

        return payload;
    }

    /// <summary>Modbus-RTU 报文构造器。</summary>
    public ModbusRtuViewModel Modbus { get; }

    /// <summary>S7（ISO-on-TCP）报文构造器。</summary>
    public S7ViewModel S7 { get; }

    /// <summary>
    /// 发送任意字节并记入日志。文本发送与 Modbus 构造器共用此路径。
    /// label 为空时按当前发送模式生成预览。
    /// </summary>
    public void SendBytes(byte[] payload, string? label = null)
    {
        if (!IsOpen)
        {
            ShowWarning("未连接", "当前页签的连接尚未建立，请先连接。");
            return;
        }

        if (payload.Length == 0)
        {
            ShowWarning("内容为空", "没有可发送的数据。");
            return;
        }

        try
        {
            ITransport transport = ActiveTransport
                ?? throw new InvalidOperationException("未连接，无法发送数据。");

            int written = transport.Send(payload);
            TxBytes = transport.TotalSent;

            string preview = label
                ?? (SendHexMode
                    ? HexConverter.ToHex(payload[..Math.Min(payload.Length, 32)])
                    : (SendText.Length > 32 ? SendText[..32] + "…" : SendText));

            AppendSend(preview, written);
        }
        catch (Exception ex)
        {
            ShowWarning("发送失败", ex.Message);
        }
    }

    public void Send()
    {
        if (!IsOpen)
        {
            ShowWarning("未连接", "当前页签的连接尚未建立，请先连接。");
            return;
        }

        try
        {
            byte[] payload = BuildSendPayload();
            if (payload.Length == 0)
            {
                ShowWarning("内容为空", SendHexMode
                    ? "HEX 内容为空，请输入要发送的数据，例如：01 A0 FF"
                    : "发送内容为空，请先输入要发送的文本。");
                return;
            }

            SendBytes(payload);
            RememberSendHistory();
        }
        catch (Exception ex)
        {
            ShowWarning("发送失败", ex.Message);
        }
    }

    private void RememberSendHistory()
    {
        string content = SendHexMode ? SendHexText : SendText;
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        string entry = content.Replace("\r\n", " ⏎ ").Replace("\n", " ⏎ ");
        if (!_settings.SendHistory.Contains(entry))
        {
            _settings.SendHistory.Insert(0, entry);
            if (_settings.SendHistory.Count > 30)
            {
                _settings.SendHistory.RemoveAt(_settings.SendHistory.Count - 1);
            }
        }
    }

    public void LoadSendFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "所有文件 (*.*)|*.*|HEX 文件 (*.hex;*.txt)|*.hex;*.txt",
            InitialDirectory = Directory.Exists(_settings.LastLogDirectory)
                ? _settings.LastLogDirectory
                : null
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            string text = File.ReadAllText(dialog.FileName);
            if (HexConverter.TryParse(text, out _, out _))
            {
                SendHexText = NormalizeHex(text);
                SendHexMode = true;
                SendHexError = string.Empty;
            }
            else
            {
                SendText = text;
                SendHexMode = false;
            }

            _settings.LastLogDirectory = Path.GetDirectoryName(dialog.FileName);
        }
        catch (Exception ex)
        {
            ShowWarning("读取失败", ex.Message);
        }
    }

    private static string NormalizeHex(string text)
    {
        var sb = new StringBuilder();
        foreach (string line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(line.Trim());
        }

        return sb.ToString();
    }

    public void SendFile()
    {
        if (!IsOpen)
        {
            ShowWarning("未连接", "当前页签的连接尚未建立，请先连接。");
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "所有文件 (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            byte[] data = File.ReadAllBytes(dialog.FileName);
            int written = ActiveTransport?.Send(data) ?? 0;
            TxBytes = ActiveTransport?.TotalSent ?? TxBytes;
            AppendEntry(
                CurrentChannel,
                ShowTimestamp ? $"[{NowStamp()}] 发送文件：" : "发送文件：",
                $"{Path.GetFileName(dialog.FileName)}，共 {written} 字节",
                LogKind.Send);
        }
        catch (Exception ex)
        {
            ShowWarning("发送文件失败", ex.Message);
        }
    }

    #endregion

    #region 循环发送

    private void ToggleLoopSend()
    {
        if (_loopSendTimer.IsEnabled)
        {
            StopLoopSend();
            return;
        }

        if (!IsOpen)
        {
            ShowWarning("未连接", "当前页签的连接尚未建立，请先连接。");
            return;
        }

        try
        {
            // 先试发一次，避免内容非法时白等一个周期
            BuildSendPayload();
        }
        catch (Exception ex)
        {
            ShowWarning("内容无效", ex.Message);
            return;
        }

        LoopConfig.SentTimes = 0;
        LoopConfig.IntervalMs = Math.Max(10, LoopConfig.IntervalMs);
        _loopSendTimer.Interval = TimeSpan.FromMilliseconds(LoopConfig.IntervalMs);
        _loopSendTimer.Start();

        OnPropertyChanged(nameof(IsLoopSending));
        OnPropertyChanged(nameof(LoopButtonText));
        AppendSystem(CurrentChannel, $"[{NowStamp()}] 开始循环发送，间隔 {LoopConfig.IntervalMs} ms");
    }

    private void StopLoopSend()
    {
        if (!_loopSendTimer.IsEnabled)
        {
            return;
        }

        _loopSendTimer.Stop();
        OnPropertyChanged(nameof(IsLoopSending));
        OnPropertyChanged(nameof(LoopButtonText));
        AppendSystem(CurrentChannel, $"[{NowStamp()}] 停止循环发送，共发送 {LoopConfig.SentTimes} 次");
    }

    private void OnLoopSendTick()
    {
        if (LoopConfig.Count > 0 && LoopConfig.SentTimes >= LoopConfig.Count)
        {
            StopLoopSend();
            return;
        }

        try
        {
            byte[] payload = BuildSendPayload();
            if (payload.Length > 0)
            {
                ActiveTransport?.Send(payload);
                TxBytes = ActiveTransport?.TotalSent ?? TxBytes;
                LoopConfig.SentTimes++;
                OnPropertyChanged(nameof(LoopConfig));
            }
            else
            {
                StopLoopSend();
            }
        }
        catch (Exception ex)
        {
            StopLoopSend();
            ShowWarning("循环发送中断", ex.Message);
        }
    }

    #endregion

    #region 辅助

    private void RefreshCommands()
    {
        OpenCommand.RaiseCanExecuteChanged();
        CloseCommand.RaiseCanExecuteChanged();
        OpenNetworkCommand.RaiseCanExecuteChanged();
        CloseNetworkCommand.RaiseCanExecuteChanged();
        OpenS7Command.RaiseCanExecuteChanged();
        CloseS7Command.RaiseCanExecuteChanged();
        SendCommand.RaiseCanExecuteChanged();
        SendFileCommand.RaiseCanExecuteChanged();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:0.##} KB";
        }

        return $"{bytes / 1024.0 / 1024.0:0.##} MB";
    }

    private static string ParityShort(Parity parity) => parity switch
    {
        Parity.None => "N",
        Parity.Odd => "O",
        Parity.Even => "E",
        _ => "M"
    };

    private static string StopShort(StopBits stopBits) => stopBits switch
    {
        StopBits.OnePointFive => "1.5",
        StopBits.Two => "2",
        _ => "1"
    };

    private void ShowWarning(string title, string message)
        => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    private void ShowInfo(string title, string message)
        => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    /// <summary>窗口关闭时保存配置。</summary>
    public void SaveSettings()
    {
        _settings.Serial = Serial.Clone();
        _settings.Network = Network.Clone();
        _settings.S7 = S7SettingsModel.Clone();
        SettingsService.Save(_settings);
    }

    public void Dispose()
    {
        _disposed = true;
        SaveSettings();
        _rxFlushTimer.Stop();
        _loopSendTimer.Stop();
        _memoryTimer.Stop();
        _privateWsCounter?.Dispose();
        _privateWsCounter = null;
        _currentProcess?.Dispose();
        _currentProcess = null;
        if (_serialTransport is not null)
        {
            _serialTransport.Dispose();
            _serialTransport = null;
        }

        if (_networkTransport is not null)
        {
            _networkTransport.Dispose();
            _networkTransport = null;
        }

        if (_s7Transport is not null)
        {
            _s7Transport.Dispose();
            _s7Transport = null;
        }
    }

    private void OnDataReceived(string tag, byte[] data)
    {
        // 由传输的后台线程回调，只做入队，实际渲染交给 UI 定时器
        lock (_pendingLock)
        {
            _pendingRx.Add((tag, data));

            // 防止极端速率下无限堆积导致内存膨胀
            int total = 0;
            for (int i = 0; i < _pendingRx.Count; i++)
            {
                total += _pendingRx[i].Data.Length;
            }

            if (total > 4 * 1024 * 1024)
            {
                int drop = 0;
                int removeCount = 0;
                while (removeCount < _pendingRx.Count && drop < 2 * 1024 * 1024)
                {
                    drop += _pendingRx[removeCount].Data.Length;
                    removeCount++;
                }

                _pendingRx.RemoveRange(0, removeCount);
            }
        }
    }

    /// <summary>热键：Ctrl+Enter 发送。</summary>
    public void HandleSendShortcut()
    {
        if (IsOpen)
        {
            Send();
        }
    }

    /// <summary>热键：Ctrl+O 切换连接。</summary>
    public void HandleToggleConnectionShortcut() => ToggleOpen();

    #endregion
}
