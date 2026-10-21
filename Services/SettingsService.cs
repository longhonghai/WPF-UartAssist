using System.IO;
using System.IO.Ports;
using System.Text.Json;
using WPF_UartAssist.Models;

namespace WPF_UartAssist.Services;

public sealed class AppSettings
{
    public SerialPortSettings Serial { get; set; } = new();

    /// <summary>默认按 ASCII 发送（串口设备通用编码）。</summary>
    public string EncodingName { get; set; } = "ASCII";

    /// <summary>界面主题：Dark / Light。</summary>
    public string Theme { get; set; } = ThemeManager.Dark;

    /// <summary>TCP/UDP 参数与上次使用的通讯方式。</summary>
    public NetworkSettings Network { get; set; } = new();

    /// <summary>S7 连接参数（地址、端口、机架、槽位）。</summary>
    public S7Settings S7 { get; set; } = new();

    public bool ReceiveHexMode { get; set; }    public bool ShowTimestamp { get; set; } = true;
    public bool TimestampWithMs { get; set; } = true;
    public bool SendHexMode { get; set; }
    public bool AppendNewLine { get; set; } = true;
    public bool AutoScroll { get; set; } = true;
    public bool WordWrap { get; set; }
    public bool AutoClearOnOpen { get; set; } = true;
    public int MaxLogLines { get; set; } = 5000;
    public double FontSize { get; set; } = 13;
    public string ReceiveFilter { get; set; } = string.Empty;
    public string? LastLogDirectory { get; set; }
    public List<string> SendHistory { get; set; } = [];
    public List<string> SavedPorts { get; set; } = [];
}

public static class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WPF-UartAssist",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            string path = SettingsPath;
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            // 配置损坏时退回默认值，不影响启动
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            string path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch
        {
            // 静默失败：配置持久化不影响主流程
        }
    }
}
