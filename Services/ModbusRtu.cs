namespace WPF_UartAssist.Services;

/// <summary>
/// Modbus-RTU 报文构造。
/// 帧结构：从站地址(1) + 功能码(1) + 数据 + CRC16(2，低字节在前)。
/// CRC16 采用 Modbus 标准参数：初值 0xFFFF，多项式 0xA001（即反转的 0x8005）。
/// </summary>
public static class ModbusRtu
{
    /// <summary>读/写支持的功能码。</summary>
    public static readonly (byte Code, string Label)[] Functions =
    [
        (0x01, "01 读线圈"),
        (0x02, "02 读离散输入"),
        (0x03, "03 读保持寄存器"),
        (0x04, "04 读输入寄存器"),
        (0x05, "05 写单个线圈"),
        (0x06, "06 写单个寄存器"),
        (0x0F, "0F 写多个线圈"),
        (0x10, "10 写多个寄存器"),
    ];

    /// <summary>Modbus 标准 CRC16。返回 [低字节, 高字节]。</summary>
    public static byte[] Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;

        foreach (byte b in data)
        {
            crc ^= b;

            for (int bit = 0; bit < 8; bit++)
            {
                if ((crc & 1) != 0)
                {
                    crc = (ushort)((crc >> 1) ^ 0xA001);
                }
                else
                {
                    crc >>= 1;
                }
            }
        }

        return [(byte)(crc & 0xFF), (byte)(crc >> 8)];
    }

    /// <summary>
    /// 构造 PDU（功能码 + 地址 + 数量/数据），RTU 与 TCP 共用。
    /// </summary>
    public static byte[] BuildPdu(byte function, ushort address, ushort quantity, byte[]? writeData)
    {
        var pdu = new List<byte>(12)
        {
            function,
            (byte)(address >> 8),
            (byte)(address & 0xFF)
        };

        if (function is 0x0F or 0x10)
        {
            int dataLength = writeData?.Length ?? 0;

            pdu.Add((byte)(quantity >> 8));
            pdu.Add((byte)(quantity & 0xFF));
            pdu.Add((byte)dataLength);            // 字节数

            if (writeData is not null)
            {
                pdu.AddRange(writeData);
            }
        }
        else
        {
            // 01/02/03/04 的数量，05/06 的写入值
            pdu.Add((byte)(quantity >> 8));
            pdu.Add((byte)(quantity & 0xFF));
        }

        return pdu.ToArray();
    }

    /// <summary>
    /// 构造 Modbus TCP（MBAP + PDU）请求帧：事务号(2) + 协议号(2，恒 0) + 长度(2) + 单元号(1) + PDU，无 CRC。
    /// </summary>
    public static byte[] BuildTcpRequest(ushort transactionId, byte unitId, byte[] pdu)
    {
        ushort length = (ushort)(pdu.Length + 1);   // 单元号 + PDU

        var frame = new byte[7 + pdu.Length];
        frame[0] = (byte)(transactionId >> 8);
        frame[1] = (byte)(transactionId & 0xFF);
        frame[2] = 0;                               // 协议标识
        frame[3] = 0;
        frame[4] = (byte)(length >> 8);
        frame[5] = (byte)(length & 0xFF);
        frame[6] = unitId;

        Buffer.BlockCopy(pdu, 0, frame, 7, pdu.Length);
        return frame;
    }

    /// <summary>
    /// 构造主站请求帧。
    /// quantity 对读功能码是寄存器/线圈数量；对 05/06 是写入值；对 0F/10 是写入数量。
    /// </summary>
    public static byte[] BuildRequest(byte slaveId, byte function, ushort address, ushort quantity, byte[]? writeData)
    {
        byte[] pdu = BuildPdu(function, address, quantity, writeData);

        byte[] body = new byte[pdu.Length + 1];
        body[0] = slaveId;
        Buffer.BlockCopy(pdu, 0, body, 1, pdu.Length);

        byte[] crc = Crc16(body);

        var result = new byte[body.Length + 2];
        Buffer.BlockCopy(body, 0, result, 0, body.Length);
        result[body.Length] = crc[0];
        result[body.Length + 1] = crc[1];
        return result;
    }

    /// <summary>
    /// 解析地址/数量/数值输入。支持十进制与 0x/0X 前缀的十六进制，
    /// 与 UartAssist 的输入习惯一致。
    /// </summary>
    public static bool TryParseUshort(string? input, out ushort value)
    {
        value = 0;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        string text = input.Trim();

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            string hex = text[2..];

            if (hex.Length is 0 or > 4)
            {
                return false;
            }

            if (!hex.All(c => Uri.IsHexDigit(c)))
            {
                return false;
            }

            value = Convert.ToUInt16(hex, 16);
            return true;
        }

        // 纯数字按十进制
        if (text.All(char.IsDigit) && text.Length <= 5)
        {
            return ushort.TryParse(text, out value);
        }

        // 容忍直接输入的十六进制字母形式（如 "FF00"）
        if (text.Length <= 4 && text.All(Uri.IsHexDigit) && text.Any(char.IsLetter))
        {
            value = Convert.ToUInt16(text, 16);
            return true;
        }

        return false;
    }

    /// <summary>把功能码下拉项（如 "03 读保持寄存器"）还原为功能码字节。</summary>
    public static byte ExtractFunctionCode(string label)
        => byte.TryParse(label.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var code)
            ? code
            : (byte)0x03;
}
