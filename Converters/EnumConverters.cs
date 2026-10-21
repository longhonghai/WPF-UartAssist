using System.Globalization;
using System.IO.Ports;
using System.Windows.Data;

namespace WPF_UartAssist.Converters;

/// <summary>把 Parity 枚举转成串口惯用的单字母显示：None→N、Odd→O、Even→E。</summary>
public sealed class ParityToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Parity.None => "无校验 N",
        Parity.Odd => "奇校验 O",
        Parity.Even => "偶校验 E",
        Parity.Mark => "标记 M",
        Parity.Space => "空格 S",
        _ => value?.ToString() ?? string.Empty
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>把 StopBits 枚举转成 1 / 1.5 / 2。</summary>
public sealed class StopBitsToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        StopBits.One => "1 位",
        StopBits.OnePointFive => "1.5 位",
        StopBits.Two => "2 位",
        _ => value?.ToString() ?? string.Empty
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>把 Handshake 枚举转成中文说明。</summary>
public sealed class HandshakeToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Handshake.None => "无",
        Handshake.XOnXOff => "软件流控 XON/XOFF",
        Handshake.RequestToSend => "硬件流控 RTS",
        Handshake.RequestToSendXOnXOff => "硬件+软件",
        _ => value?.ToString() ?? string.Empty
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
