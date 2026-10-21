using System.Text;
using System.Text.Json.Serialization;

namespace WPF_UartAssist.Models;

/// <summary>定时循环发送配置。</summary>
public sealed class LoopSendConfig
{
    public int IntervalMs { get; set; } = 1000;

    public int Count { get; set; }

    [JsonIgnore]
    public int SentTimes { get; set; }
}
