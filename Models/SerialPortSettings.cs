using System.IO.Ports;
using System.Text;
using System.Text.Json.Serialization;

namespace WPF_UartAssist.Models;

/// <summary>串口连接参数。</summary>
public sealed class SerialPortSettings
{
    public string PortName { get; set; } = "COM1";

    public int BaudRate { get; set; } = 115200;

    public Parity Parity { get; set; } = Parity.None;

    public int DataBits { get; set; } = 8;

    public StopBits StopBits { get; set; } = StopBits.One;

    public Handshake Handshake { get; set; } = Handshake.None;

    [JsonIgnore]
    public Encoding Encoding { get; set; } = Encoding.UTF8;

    public SerialPortSettings Clone() => (SerialPortSettings)MemberwiseClone();
}
