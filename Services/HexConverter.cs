using System.Text;

namespace WPF_UartAssist.Services;

/// <summary>HEX 字符串与字节数组互转工具，容忍空格、逗号、0x 前缀与换行。</summary>
public static class HexConverter
{
    private static bool TryHexDigit(char c, out int value)
    {
        if (c is >= '0' and <= '9') { value = c - '0'; return true; }
        if (c is >= 'a' and <= 'f') { value = c - 'a' + 10; return true; }
        if (c is >= 'A' and <= 'F') { value = c - 'A' + 10; return true; }
        value = 0;
        return false;
    }

    /// <summary>
    /// 解析 HEX 文本。容忍空格/逗号/换行分隔，以及 0x、\x 前缀。
    /// 遇到非法字符或半截字节时返回 false，并用 errorIndex 指出位置。
    /// </summary>
    public static bool TryParse(string? input, out byte[] bytes, out int errorIndex)
    {
        errorIndex = -1;
        bytes = [];

        if (string.IsNullOrWhiteSpace(input))
        {
            return true;
        }

        var result = new List<byte>(input.Length / 2);
        int i = 0;

        while (i < input.Length)
        {
            char c = input[i];

            // 分隔符直接跳过
            if (char.IsWhiteSpace(c) || c is ',' or ';' or ':' or '-')
            {
                i++;
                continue;
            }

            // 0x / \x 前缀：跳过前缀后必须紧跟一个完整的字节
            if (c == '\\' && i + 2 < input.Length && (input[i + 1] is 'x' or 'X'))
            {
                if (!TryReadByte(input, i + 2, out byte prefixed, out int prefixError))
                {
                    errorIndex = prefixError;
                    bytes = [];
                    return false;
                }

                result.Add(prefixed);
                i += 4;
                continue;
            }

            if (c == '0' && i + 1 < input.Length && (input[i + 1] is 'x' or 'X'))
            {
                if (!TryReadByte(input, i + 2, out byte prefixed, out int prefixError))
                {
                    errorIndex = prefixError;
                    bytes = [];
                    return false;
                }

                result.Add(prefixed);
                i += 4;
                continue;
            }

            if (!TryHexDigit(c, out int high))
            {
                errorIndex = i;
                bytes = [];
                return false;
            }

            // 高位已读，等待紧接着的低位；跨分隔符不允许（避免 "A BB" 被误读）
            i++;
            if (i >= input.Length || !TryHexDigit(input[i], out int low))
            {
                errorIndex = Math.Min(i, input.Length - 1);
                bytes = [];
                return false;
            }

            result.Add((byte)((high << 4) | low));
            i++;
        }

        bytes = result.ToArray();
        return true;
    }

    /// <summary>从 start 处读取恰好两位 HEX 组成一个字节。</summary>
    private static bool TryReadByte(string input, int start, out byte value, out int errorIndex)
    {
        value = 0;

        if (start + 1 >= input.Length ||
            !TryHexDigit(input[start], out int high) ||
            !TryHexDigit(input[start + 1], out int low))
        {
            errorIndex = Math.Min(start, input.Length - 1);
            return false;
        }

        value = (byte)((high << 4) | low);
        errorIndex = -1;
        return true;
    }

    /// <summary>格式化为标准 HEX 字符串：AA BB CC。</summary>
    public static string ToHex(ReadOnlySpan<byte> data, char separator = ' ')
    {
        if (data.IsEmpty)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(data.Length * 3);
        for (int i = 0; i < data.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(separator);
            }

            sb.Append(data[i].ToString("X2"));
        }

        return sb.ToString();
    }

    /// <summary>
    /// 按 bytesPerLine 分行的纯 HEX 文本（不含偏移量与 ASCII 对照列）。
    /// 一行以内时与 ToHex 相同。
    /// </summary>
    public static string ToHexLines(ReadOnlySpan<byte> data, int bytesPerLine = 16)
    {
        if (data.IsEmpty)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();

        for (int offset = 0; offset < data.Length; offset += bytesPerLine)
        {
            int count = Math.Min(bytesPerLine, data.Length - offset);

            if (offset > 0)
            {
                sb.AppendLine();
            }

            sb.Append(ToHex(data.Slice(offset, count)));
        }

        return sb.ToString();
    }
}
