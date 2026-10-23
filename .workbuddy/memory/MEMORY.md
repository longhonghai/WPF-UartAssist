# WPF-UartAssist 项目长期记忆

## 项目定位
WPF 串口调试助手，对标 UartAssist。net9.0-windows + WPF + MVVM，无第三方 UI 框架。

## 环境事实（本机）
- **虚拟串口**：已装 ELTIMA Virtual Serial Port，存在两对互连端口
  - `COM1 ↔ COM2`
  - `COM3 ↔ COM4`
  - 已实测 COM3→COM4 与 COM4→COM3 双向可通（发送 8 字节 Modbus 帧，对端完整收到）
  - 用途：双实例自测收发、验证协议帧，无需真实硬件
- 系统区域 zh-CN，ANSI 代码页 936（GBK）

## 关键约定
- **「ASCII」编码 = 系统 ANSI 代码页**，不是 .NET 严格 7-bit ASCII。
  这是 UartAssist 的真实语义（"文本模式"），中文才能正常收发。
  严格 ASCII 会把中文变成 `3F 3F`。
- 日志格式：`[HH:mm:ss.fff] 接收 N 字节：内容`，发送/接收/系统三类分色
  （绿 / 蓝 / 灰），前缀暗灰。
- Modbus-RTU 报文不追加换行符（`AppendNewLine` 只作用于文本发送）。
- 勾选/取消「Modbus-RTU 构造器」会同步勾选/取消「HEX 显示」与「HEX 发送」。

## 主题架构（暗/亮切换）
- 三段式：`Themes/Dark.xaml`、`Themes/Light.xaml` 只放 **Color + SolidColorBrush**（键名必须完全一致）；
  `Themes/Styles.xaml` 放全部样式，颜色一律 `DynamicResource`，字体/边距仍 `StaticResource`。
- `ThemeManager.Apply("Dark"/"Light")` 替换 `Application.Resources.MergedDictionaries` 里那份调色板字典，
  样式字典只加载一次 → 切换即时生效，无需重建窗口。
- **新增颜色时两个 palette 都要加同名键**，否则切换后该处回退系统默认色（黑/白）。
- `MainWindow.xaml` 里直接用画刷的地方必须写 `DynamicResource`；写成 `StaticResource` 的不会跟随切换。
- 日志三类分色走 `LogReceive/LogSend/LogSystem/LogPrefix` 四个 palette 键，
  由 code-behind `RefreshLogBrushes()` 取值；`Paragraph.Tag` 存 LogKind，切主题时按 Tag 重着色已有行。
- 状态灯配色不用转换器，改用 `DataTrigger` + `DynamicResource`，否则切主题不刷新。
- 主题存 `settings.json` 的 `Theme` 字段，`App.OnStartup` 在建窗口前套用，避免启动闪暗色。

## 性能约定（启动耗时）
- **禁止在 UI 线程/构造函数里做性能计数器枚举**：`new PerformanceCounterCategory("Process").GetInstanceNames()`
  实测约 1 秒。所有 `PerformanceCounter` 探测必须放后台线程，结果回 UI 线程赋值。
- 启动基线（Release，热启）：压缩单文件 ~1.0s；不压缩单文件 ~0.8s；文件夹发布 ~0.8s。
  分段：runtime 加载 ~434ms / XAML 解析 ~237ms / 首帧 ~293ms / VM 构造 ~9ms。
- 单文件冷启动 ~2.4s 属预期（每个版本首次运行解压到 %TEMP%\.net），之后恢复正常。
- 测量启动：外部 `Start-Process` + 40ms 轮询 `MainWindowHandle != 0`，勿用 `timeout` 返回值判断。

## 传输层（串口 / TCP / UDP / S7 / MQTT / OPC UA / DoIP）
- `ITransport` 统一抽象：DataReceived / ErrorOccurred / ConnectionStateChanged + IsOpen / TotalReceived / TotalSent / Description + Open() / Close() / Send()。
  ViewModel 只持有 `_transport`，收发管道（队列→50ms 批量→日志→统计）完全与传输无关。
- 六条连接并存：`_serialTransport`(0) / `_networkTransport`(1) / `_s7Transport`(2) / `_mqttTransport`(3) / `_opcUaTransport`(4) / `_doIpTransport`(5)，
  另有独立的 `_opcUaServer`（`OpcUaServerHost`，内置服务端，不算通道），
  页签索引 == `LogChannel` 值 == 槽位号，三者必须始终对齐。
- **加新通道时必查**：`MainWindow._documents`、`MainWindow._widestLineWidths`、`MainViewModel._logLines`、
  `_rxLinesPerChannel` 的数组长度，以及 `OpenTransport` 的 `switch (slot)` —— 少一个 case 会静默串到 default 分支。
  这类越界**编译不报错**，只在切页签/写日志时抛 `IndexOutOfRangeException`。
- 实现：`SerialPortService`（后台泵线程）、`TcpClientTransport`、`TcpServerTransport`（广播给所有客户端）、`UdpTransport`（保留报文边界）、`S7Transport`（COTP 握手 + PDU 协商）、`MqttTransport`。
- `ITopicTransport : ITransport` 是主题型协议的扩展（目前只有 MQTT）：加 `TopicMessageReceived` 事件与 `Publish(topic,payload,qos,retain)`；
  这类传输**不投递** `DataReceived`，否则同一条消息记两遍。
- **TCP 客户端连接超时**：`ConnectAsync().Wait(5000)` 返回 false 时必须 `Dispose` client 才能取消，否则连接任务继续占用。
- 意外断线走 `ConnectionStateChanged(false)` → `_dispatcher.BeginInvoke`；主动关闭时先判 `IsOpen`，避免重复提示。
- 顶部参数区是 TabControl（串口 / TCP·UDP / S7 / MQTT / OPC UA / DoIP），样式 `MainTabControl`、`MainTabItem` 在 Styles.xaml。
- 主 Grid 行：0 标题 / 1 页签 / 2 日志(*) / 3 Modbus / 4 S7 / 5 MQTT 主题 / 6 OPC UA / 7 DoIP / 8 发送区 / 9 状态栏。

## OPC UA 备忘（1.5.374.78，勿升级）
- 必须钉 `OPCFoundation.NetStandard.Opc.Ua.Client/Server 1.5.374.78`；1.5.375+ 与 2.0.x 是异步重构，API 全变。
- `CoreClientUtils` 在 `Opc.Ua.Client` 命名空间；`Session` 要写全 `Opc.Ua.Client.Session`（有歧义）。
- `Session.Create(...)` 返回 `Task<Session>`，要 await；Read/Write 是 out 参数的老式同步调用。
- **禁止在 UI 线程直接调 OPC UA 栈**（`Start` / `Session.Create` / `Read` / `Write` 都会卡死），
  一律 `Task.Run(...).GetAwaiter().GetResult()`。
- 证书在 `%APPDATA%\WPF-UartAssist\pki`，首次启动自动生成；服务端 ns 索引实测 2。
- OPC UA 不是字节流：该页签隐藏发送区，`Send()` 抛 NotSupported。

## DoIP 备忘（ISO 13400-2）
- 8 字节头：版本(0x02) + 取反(0xFD) + 负载类型(2) + 长度(4，大端)，PDU 紧随其后。
- 0x0005 路由激活 → 0x0006 响应（应答码 0x00 已同意）；0x8001 诊断报文（SA/TA + UDS）。
- 收数据必须**按帧切**：一个 TCP 段可能含多帧，也要处理半帧继续等。
- `DataReceived` 空实现，只走 `FrameReceived`，否则同一帧记两遍。
- 车辆识别/存活检查在标准里还涉及 UDP 13400 广播，目前只实现 TCP。

## MQTTnet 5.x 备忘
- 类型全在根命名空间 `MQTTnet`；`MqttClientFactory` 建客户端；事件是 `Func<T,Task>`。
- `Payload` 是 `ReadOnlySequence<byte>`，用 `.ToArray()`。
- 凭据 `builder.WithCredentials(user, bytes)`；TLS 用 `builder.WithTlsOptions(t => t.UseTls().WithTargetHost(...))`（builder DSL，不是给 options 赋属性）。
- QoS 是枚举 `MqttQualityOfServiceLevel`；接口层 `Publish` 用 int，需要强转。设置里存「0 最多一次」标签，解析取前导数字。

## 多实例注意
- 程序未做单实例限制，可多开。
- 但多个实例共用 `%APPDATA%\WPF-UartAssist\settings.json`，退出时互相覆盖（后关的赢）。
  若长期双开，可考虑加 `--profile=` 参数隔离配置。
- 同一 COM 口不能同时被两个实例打开（Windows 串口独占）。

## 验证方法学（踩过坑，务必遵守）
- **不要用 `timeout N ./xxx.exe` 返回 124 判断 GUI 正常**——只能证明进程活着，证不了窗口存在。
  必须读 `$p.MainWindowHandle`（非 0 才是真的创建了窗口）。
- 本机 **禁止 `Add-Type`**（含 `-AssemblyName`），无法截屏。
- 外部探针项目易受干扰；**最可靠的是给主程序加临时 `--diag` 开关**：
  在真实进程内 `Show()` 窗口后遍历视觉树断言，写 `diag-report.txt`，验证完删除。
- 无真实串口时测收发逻辑：用反射调用私有 `AppendLog`/`AppendSend`/`AppendSystem`，
  或直接用 `[System.IO.Ports.SerialPort]` 走 COM3/COM4 虚拟对。
- 临时测试程序**不要调用 `MainViewModel.Dispose()`**（会触发 `SaveSettings()` 回写配置）。
