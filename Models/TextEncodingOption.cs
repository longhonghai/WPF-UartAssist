using System.Globalization;
using System.Text;

namespace WPF_UartAssist.Models;

/// <summary>文本编码选项。</summary>
public sealed record TextEncodingOption(string Name, string Charset, Func<Encoding> Factory)
{
    static TextEncodingOption()
    {
        // .NET Core 默认只内置 UTF/ASCII/Latin1，GBK、Big5 等代码页必须先注册，
        // 否则 Encoding.GetEncoding(936) 会抛 NotSupportedException。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public Encoding Create() => Factory();

    public static readonly TextEncodingOption[] All =
    [
        // 默认项。名字沿用 UartAssist 的叫法，实际按系统 ANSI 代码页编解码，
        // 因此中文可以正常收发（详见 GetAnsiEncoding 注释）。
        new("ASCII", "ansi", GetAnsiEncoding),
        new("UTF-8", "utf-8", () => new UTF8Encoding(false)),
        new("GBK", "gb2312", () => Encoding.GetEncoding(936)),
        new("GB2312", "gb2312", () => Encoding.GetEncoding(936)),
        new("Big5", "big5", () => Encoding.GetEncoding(950)),
        new("UTF-16LE", "utf-16", () => Encoding.Unicode),
        new("Latin-1", "iso-8859-1", () => Encoding.Latin1),
    ];

    /// <summary>
    /// 取系统 ANSI 代码页（中文 Windows 上即 GBK / 936）。
    ///
    /// 这正是 UartAssist 等工具"ASCII 模式"的实际行为：那个"ASCII"只是相对
    /// "HEX 模式"而言的"文本模式"称呼，底层用本地代码页编解码，所以能显示中文；
    /// 而 0x00-0x7F 范围内与严格 ASCII 字节完全一致，AT 指令等纯英文数据不受影响。
    /// 严格 7-bit ASCII 反而会把中文替换成 '?'，与 UartAssist 的行为不一致。
    /// </summary>
    private static Encoding GetAnsiEncoding()
    {
        int codePage = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;

        try
        {
            return Encoding.GetEncoding(codePage);
        }
        catch
        {
            // 极少见的区域设置下取不到，退回简体中文代码页
            return Encoding.GetEncoding(936);
        }
    }

    public static TextEncodingOption Resolve(string? name)
        => All.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
           ?? All.First(x => x.Name == "ASCII");

    public static string Describe(Encoding encoding)
    {
        string webName = encoding.WebName;

        // gb2312 同时对应 GBK 与 GB2312 两个选项，优先报通用名
        TextEncodingOption? match = All.FirstOrDefault(x =>
            x.Charset.Equals(webName, StringComparison.OrdinalIgnoreCase));

        return match?.Name ?? webName;
    }
}
