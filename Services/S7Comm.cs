using WPF_UartAssist.Models;

namespace WPF_UartAssist.Services;

/// <summary>
/// S7comm（ISO-on-TCP / RFC1006）报文构造与解析。
/// 分层：TPKT(4) + COTP(1~3) + S7 头(10 或 12) + 参数区 + 数据区。
/// </summary>
public static class S7Comm
{
    // ===== S7 头 =====
    public const byte ProtocolId = 0x32;

    public const byte RosctrJob = 0x01;       // 请求
    public const byte RosctrAck = 0x02;       // 确认
    public const byte RosctrAckData = 0x03;   // 带数据确认

    public const byte JobHeaderSize = 10;     // Job 头长度
    public const byte AckHeaderSize = 12;     // Ack/Ack_Data 头长度（含错误类与错误码）

    // ===== 功能码 =====
    public const byte FuncReadVar = 0x04;
    public const byte FuncWriteVar = 0x05;
    public const byte FuncSetupCommunication = 0xF0;

    /// <summary>Ack_Data 参数区的返回码：成功。</summary>
    public const byte ReturnCodeSuccess = 0xFF;

    /// <summary>协商 PDU 长度时申请的字节数（实际生效值由 PLC 回复决定）。</summary>
    public const ushort RequestedPduSize = 480;

    // ===== 存储区 =====
    public static readonly (string Label, byte Code)[] Areas =
    [
        ("DB 数据块", 0x84),
        ("M 位存储器", 0x83),
        ("I 输入过程映像", 0x81),
        ("Q 输出过程映像", 0x82),
        ("C 计数器", 0x1C),
        ("T 定时器", 0x1D),
    ];

    // ===== 数据类型 =====
    /// <summary>
    /// 数据类型。<see cref="ItemCode"/> 用于请求项里的「传输大小」，
    /// <see cref="DataCode"/> 用于数据区里的「传输大小」，两套编码并不相同。
    /// </summary>
    public readonly record struct DataType(
        string Label, byte ItemCode, byte DataCode, int Size, string Unit);

    /// <summary>Size 为单个元素占用的字节数；BOOL 特殊，按位寻址。</summary>
    public static readonly DataType[] DataTypes =
    [
        new("BOOL", 0x01, 0x03, 1, "位"),
        new("BYTE", 0x02, 0x04, 1, "字节"),
        new("CHAR", 0x03, 0x09, 1, "字节"),
        new("WORD", 0x04, 0x04, 2, "字节"),
        new("INT", 0x05, 0x05, 2, "字节"),
        new("DWORD", 0x06, 0x04, 4, "字节"),
        new("DINT", 0x07, 0x06, 4, "字节"),
        new("REAL", 0x08, 0x07, 4, "字节"),
    ];

    // ===== TPKT / COTP =====

    /// <summary>
    /// COTP 连接请求（CR）。TPKT 长度字段含 TPKT 首部本身。
    /// 目标 TSAP 的第二字节 = 机架 × 32 + 槽位。
    /// </summary>
    public static byte[] BuildConnectionRequest(int rack, int slot)
    {
        var frame = new List<byte>(22)
        {
            0x03, 0x00, 0x00, 0x16,   // TPKT：版本 3，总长 22
            0x11,                     // COTP LI：后续 17 字节
            0xE0,                     // PDU 类型 = CR
            0x00, 0x00,               // DST-REF
            0x00, 0x01,               // SRC-REF
            0x00,                     // CLASS
            0xC0, 0x01, 0x0A,         // TPDU Size = 1024
            0xC1, 0x02, 0x01, 0x00,   // 源 TSAP：PG/PC
            0xC2, 0x02, 0x03, (byte)((rack << 5) | slot), // 目标 TSAP：设备组 3 + 机架槽位
        };

        return frame.ToArray();
    }

    /// <summary>把 S7 PDU 包进 COTP 数据传输（DT）与 TPKT 首部。</summary>
    public static byte[] WrapCotp(byte[] s7Pdu)
    {
        int total = 4 + 3 + s7Pdu.Length;
        var frame = new byte[total];

        frame[0] = 0x03;
        frame[1] = 0x00;
        frame[2] = (byte)(total >> 8);
        frame[3] = (byte)(total & 0xFF);
        frame[4] = 0x02;   // COTP LI
        frame[5] = 0xF0;   // PDU 类型 = DT
        frame[6] = 0x80;   // TPDU-NR = 0，EOT

        Buffer.BlockCopy(s7Pdu, 0, frame, 7, s7Pdu.Length);
        return frame;
    }

    /// <summary>
    /// 从 TCP 流中取出一个完整的 TPKT 帧。数据不足时返回 0。
    /// TPKT 前 4 字节为「03 00 长度(大端)」，长度含首部。
    /// </summary>
    public static int TryReadTpkt(ReadOnlySpan<byte> buffer, out byte[] frame)
    {
        frame = [];

        if (buffer.Length < 4 || buffer[0] != 0x03 || buffer[1] != 0x00)
        {
            return 0;
        }

        int total = (buffer[2] << 8) | buffer[3];

        if (total < 7 || buffer.Length < total)
        {
            return 0;
        }

        frame = buffer[..total].ToArray();
        return total;
    }

    /// <summary>剥掉 TPKT + COTP，得到 S7 PDU。格式不符时返回 null。</summary>
    public static byte[]? UnwrapCotp(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 7 || frame[4] != 0x02 || frame[5] != 0xF0)
        {
            return null;
        }

        return frame[7..].ToArray();
    }

    // ===== 请求构造 =====

    /// <summary>协商 PDU 长度（建立 COTP 连接后应先发这个）。</summary>
    public static byte[] BuildSetupCommunication(ushort pduReference)
    {
        byte[] param =
        [
            FuncSetupCommunication,
            0x00,
            0x00, 0x01,       // Max AmQ（调用方）
            0x00, 0x01,       // Max AmQ（被调用方）
            0x01, (byte)(RequestedPduSize >> 8), (byte)(RequestedPduSize & 0xFF),
        ];

        return BuildJob(pduReference, param, []);
    }

    /// <summary>读变量（功能码 0x04）。count 为元素个数。</summary>
    public static byte[] BuildReadRequest(
        ushort pduReference, byte area, ushort dbNumber,
        int byteAddress, int bitAddress, DataType type, int count)
    {
        byte[] param = BuildItem(FuncReadVar, area, dbNumber, byteAddress, bitAddress, type, count);
        return BuildJob(pduReference, param, []);
    }

    /// <summary>
    /// 写变量（功能码 0x05）。data 为待写入的原始字节。
    /// </summary>
    public static byte[] BuildWriteRequest(
        ushort pduReference, byte area, ushort dbNumber,
        int byteAddress, int bitAddress, DataType type, byte[] data)
    {
        int elementCount = Math.Max(1, data.Length / Math.Max(1, type.Size));
        byte[] param = BuildItem(FuncWriteVar, area, dbNumber, byteAddress, bitAddress, type, elementCount);

        // 数据区：保留(0x00) + 传输大小 + 长度 + 数据(+ 奇数字节补 0)
        var dataSection = new List<byte>(4 + data.Length + 1)
        {
            0x00,
            type.DataCode,
        };

        // 数据区的长度单位：传输大小 0x03/0x04 为位，其余为字节
        int length = type.DataCode is 0x03 or 0x04 ? data.Length * 8 : data.Length;
        dataSection.Add((byte)(length >> 8));
        dataSection.Add((byte)(length & 0xFF));
        dataSection.AddRange(data);

        // S7 数据区按偶数字节对齐
        if (dataSection.Count % 2 != 0)
        {
            dataSection.Add(0x00);
        }

        return BuildJob(pduReference, param, dataSection.ToArray());
    }

    /// <summary>
    /// 构造一个请求项（Item）。地址字段是 3 字节「位地址」= 字节地址 × 8 + 位偏移。
    /// </summary>
    private static byte[] BuildItem(
        byte function, byte area, ushort dbNumber,
        int byteAddress, int bitAddress, DataType type, int count)
    {
        int bitOffset = type.ItemCode == 0x01
            ? Math.Clamp(bitAddress, 0, 7)              // BOOL 直接按位寻址
            : (byteAddress * 8 + Math.Clamp(bitAddress, 0, 7));

        // 非 DB 区域不写 DB 号
        ushort itemDb = area == 0x84 ? dbNumber : (ushort)0;

        return
        [
            function,
            0x01,                   // 项数
            0x12,                   // 变量规格
            0x0A,                   // 后续地址规格长度 = 10
            0x10,                   // 语法 ID = S7ANY
            type.ItemCode,
            (byte)(count >> 8), (byte)(count & 0xFF),
            (byte)(itemDb >> 8), (byte)(itemDb & 0xFF),
            area,
            (byte)(bitOffset >> 16), (byte)(bitOffset >> 8), (byte)bitOffset,
        ];
    }

    /// <summary>组装 S7 头 + 参数区 + 数据区（不含 TPKT/COTP）。</summary>
    private static byte[] BuildJob(ushort pduReference, byte[] param, byte[] data)
    {
        var pdu = new byte[JobHeaderSize + param.Length + data.Length];
        int i = 0;

        pdu[i++] = ProtocolId;
        pdu[i++] = RosctrJob;
        pdu[i++] = 0x00;              // 冗余标识高
        pdu[i++] = 0x00;              // 冗余标识低
        pdu[i++] = (byte)(pduReference >> 8);
        pdu[i++] = (byte)(pduReference & 0xFF);
        pdu[i++] = (byte)(param.Length >> 8);
        pdu[i++] = (byte)(param.Length & 0xFF);
        pdu[i++] = (byte)(data.Length >> 8);
        pdu[i++] = (byte)(data.Length & 0xFF);

        Buffer.BlockCopy(param, 0, pdu, i, param.Length);
        Buffer.BlockCopy(data, 0, pdu, i + param.Length, data.Length);
        return pdu;
    }

    // ===== 解析 =====

    /// <summary>S7 PDU 的头部长度。Job 为 10，Ack/Ack_Data 为 12。</summary>
    public static int GetHeaderSize(byte rosctr)
        => rosctr is RosctrAck or RosctrAckData or 0x07 ? AckHeaderSize : JobHeaderSize;

    /// <summary>返回码的中文说明。</summary>
    public static string DescribeReturnCode(byte code) => code switch
    {
        0xFF => "成功",
        0x00 => "预留",
        0x01 => "硬件故障",
        0x03 => "不允许访问该对象",
        0x05 => "地址无效",
        0x06 => "不支持该数据类型",
        0x07 => "数据类型不一致",
        0x0A => "对象不存在",
        _ => $"未知返回码 0x{code:X2}"
    };

    /// <summary>错误类的中文说明。</summary>
    public static string DescribeErrorClass(byte errorClass) => errorClass switch
    {
        0x00 => "无错误",
        0x81 => "应用关系错误",
        0x82 => "对象定义错误",
        0x83 => "无可用资源",
        0x84 => "服务处理错误",
        0x85 => "资源供给错误",
        0x87 => "访问错误",
        _ => $"错误类 0x{errorClass:X2}"
    };

    /// <summary>
    /// 把响应数据区解析成逐项的值。读响应每项为：返回码 + 传输大小 + 长度 + 数据。
    /// </summary>
    public static List<(byte ReturnCode, byte TransportSize, byte[] Data)> ParseReadItems(ReadOnlySpan<byte> data)
    {
        var items = new List<(byte, byte, byte[])>();
        int offset = 0;

        while (offset + 4 <= data.Length)
        {
            byte returnCode = data[offset];
            byte transportSize = data[offset + 1];
            int length = (data[offset + 2] << 8) | data[offset + 3];
            offset += 4;

            if (returnCode != ReturnCodeSuccess)
            {
                // 出错时后面没有数据，按返回码逐项记录即可
                items.Add((returnCode, transportSize, []));
                continue;
            }

            // 传输大小 0x03/0x04 的长度单位是位，其余是字节
            int byteLength = transportSize is 0x03 or 0x04
                ? (length + 7) / 8
                : length;

            byteLength = Math.Min(byteLength, data.Length - offset);
            items.Add((returnCode, transportSize, data.Slice(offset, byteLength).ToArray()));
            offset += byteLength;

            // 数据区按偶数字节对齐，末项之后不再补
            if (offset < data.Length && offset % 2 != 0)
            {
                offset++;
            }
        }

        return items;
    }

    /// <summary>
    /// 写应答的数据区只有一个返回码（0xFF 成功），没有数据项。
    /// </summary>
    public static bool IsWriteSuccess(ReadOnlySpan<byte> data)
        => data.Length >= 1 && data[0] == ReturnCodeSuccess;

    /// <summary>
    /// 校验一段字节能否作为 S7 PDU 发送：
    /// 以 0x32 开头视为裸 PDU（自动补 COTP/TPKT），以 0x03 开头视为完整帧（直接发）。
    /// </summary>
    public static bool IsCompleteFrame(ReadOnlySpan<byte> payload)
        => payload.Length >= 4 && payload[0] == 0x03 && payload[1] == 0x00;

    public static bool IsBarePdu(ReadOnlySpan<byte> payload)
        => payload.Length >= 2 && payload[0] == ProtocolId;

    /// <summary>
    /// 把用户输入的内容整理成可发送的字节：裸 PDU 补齐 TPKT/COTP，其余按原样发出。
    /// </summary>
    public static byte[] PrepareOutbound(ReadOnlySpan<byte> payload)
    {
        if (IsCompleteFrame(payload))
        {
            return payload.ToArray();
        }

        return IsBarePdu(payload) ? WrapCotp(payload.ToArray()) : payload.ToArray();
    }

    /// <summary>按类型把原始字节渲染成可读文本，用于解码结果显示。</summary>
    public static string FormatValue(DataType type, byte[] value)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        switch (type.ItemCode)
        {
            case 0x01: // BOOL
                return (value[0] & 0x01) != 0 ? "TRUE" : "FALSE";

            case 0x02: // BYTE
                return $"{value[0]} (0x{value[0]:X2})";

            case 0x03: // CHAR
                return $"'{System.Text.Encoding.ASCII.GetString(value)}' (0x{value[0]:X2})";

            case 0x04: // WORD 无符号 16 位
            {
                ushort word = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(value);
                return $"{word} (0x{word:X4})";
            }

            case 0x05: // INT 有符号 16 位
                return $"{System.Buffers.Binary.BinaryPrimitives.ReadInt16BigEndian(value)}";

            case 0x06: // DWORD 无符号 32 位
            {
                uint dword = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(value);
                return $"{dword} (0x{dword:X8})";
            }

            case 0x07: // DINT 有符号 32 位
                return $"{System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(value)}";

            case 0x08: // REAL
                return System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(value).ToString("0.####");

            default:
                return HexConverter.ToHex(value);
        }
    }

    /// <summary>区域代码的中文名。</summary>
    public static string DescribeArea(byte area)
        => Areas.FirstOrDefault(a => a.Code == area).Label ?? $"区域 0x{area:X2}";

    /// <summary>按标签取数据类型。</summary>
    public static DataType FindType(string label)
        => DataTypes.FirstOrDefault(t => t.Label == label);
}