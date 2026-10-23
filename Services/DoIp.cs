using System.Buffers.Binary;

namespace WPF_UartAssist.Services;

/// <summary>
/// DoIP（ISO 13400-2）报文。头部固定 8 字节：
/// 协议版本(1) + 版本取反(1) + 负载类型(2) + 负载长度(4，大端)。
/// </summary>
public sealed class DoIpFrame
{
    public DoIpFrame(byte version, ushort payloadType, byte[] payload, byte[] raw)
    {
        Version = version;
        PayloadType = payloadType;
        Payload = payload;
        Raw = raw;
    }

    /// <summary>协议版本，0x02 = ISO 13400-2:2012，0x03 = 2019。</summary>
    public byte Version { get; }

    /// <summary>负载类型，如 0x8001 诊断报文。</summary>
    public ushort PayloadType { get; }

    /// <summary>负载内容（不含 8 字节头）。</summary>
    public byte[] Payload { get; }

    /// <summary>完整原始帧（含头）。</summary>
    public byte[] Raw { get; }

    public string TypeName => DoIp.PayloadTypeName(PayloadType);

    /// <summary>源地址 / 测试端地址，随负载类型不同。</summary>
    public ushort? SourceAddress => DoIp.SourceAddressOf(PayloadType, Payload);

    /// <summary>目标地址 / DoIP 实体地址。</summary>
    public ushort? TargetAddress => DoIp.TargetAddressOf(PayloadType, Payload);

    /// <summary>诊断报文里的 UDS 数据。</summary>
    public byte[]? Uds => PayloadType == DoIp.PayloadTypeDiagnostic ? Payload[4..] : null;

    /// <summary>应答码（路由激活响应 / 诊断确认）。</summary>
    public byte? AckCode => DoIp.AckCodeOf(PayloadType, Payload);

    /// <summary>日志里显示的一行摘要。</summary>
    public string Describe()
    {
        string hex = Convert.ToHexString(Payload);
        string text = $"接收 {Raw.Length} 字节　{TypeName}（0x{PayloadType:X4}）";

        if (SourceAddress is ushort sa)
        {
            text += $"　源 0x{sa:X4}";
        }

        if (TargetAddress is ushort ta)
        {
            text += $" → 目标 0x{ta:X4}";
        }

        if (AckCode is byte code)
        {
            text += $"　{DoIp.AckCodeName(PayloadType, code)}（0x{code:X2}）";
        }

        if (Uds is { Length: > 0 } uds)
        {
            text += $"　UDS {Convert.ToHexString(uds)}";
        }
        else if (Payload.Length > 0 && AckCode is null)
        {
            text += $"　{hex}";
        }

        return text;
    }
}

/// <summary>DoIP 组帧与解析。</summary>
public static class DoIp
{
    public const byte DefaultVersion = 0x02;
    public const int HeaderSize = 8;
    public const ushort DefaultPort = 13400;

    // 负载类型
    public const ushort PayloadTypeGenericNack = 0x0000;
    public const ushort PayloadTypeVehicleIdRequest = 0x0001;
    public const ushort PayloadTypeVehicleIdRequestEid = 0x0002;
    public const ushort PayloadTypeVehicleIdRequestVin = 0x0003;
    public const ushort PayloadTypeVehicleAnnouncement = 0x0004;
    public const ushort PayloadTypeRoutingActivationRequest = 0x0005;
    public const ushort PayloadTypeRoutingActivationResponse = 0x0006;
    public const ushort PayloadTypeAliveCheckRequest = 0x0007;
    public const ushort PayloadTypeAliveCheckResponse = 0x0008;
    public const ushort PayloadTypeEntityStatusRequest = 0x4001;
    public const ushort PayloadTypeEntityStatusResponse = 0x4002;
    public const ushort PayloadTypePowerModeRequest = 0x4003;
    public const ushort PayloadTypePowerModeResponse = 0x4004;
    public const ushort PayloadTypeDiagnostic = 0x8001;
    public const ushort PayloadTypeDiagnosticAck = 0x8002;
    public const ushort PayloadTypeDiagnosticNack = 0x8003;

    /// <summary>构造器支持的报文种类（下拉显示用）。</summary>
    public static readonly (ushort Type, string Label)[] FrameKinds =
    [
        (PayloadTypeDiagnostic, "8001 诊断报文"),
        (PayloadTypeRoutingActivationRequest, "0005 路由激活请求"),
        (PayloadTypeVehicleIdRequest, "0001 车辆识别请求"),
        (PayloadTypeAliveCheckResponse, "0008 存活检查响应"),
        (PayloadTypeEntityStatusRequest, "4001 实体状态请求"),
    ];

    /// <summary>路由激活类型。</summary>
    public static readonly (byte Code, string Label)[] ActivationTypes =
    [
        (0x00, "0x00 默认"),
        (0x01, "0x01 WWH-OBD"),
        (0xE0, "0xE0 中央安全"),
    ];

    /// <summary>按负载类型拼出完整帧（含 8 字节头）。</summary>
    public static byte[] BuildFrame(byte version, ushort payloadType, byte[] payload)
    {
        var frame = new byte[HeaderSize + payload.Length];

        frame[0] = version;
        frame[1] = (byte)~version;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2, 2), payloadType);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4, 4), (uint)payload.Length);

        Buffer.BlockCopy(payload, 0, frame, HeaderSize, payload.Length);
        return frame;
    }

    /// <summary>诊断报文负载：源地址(2) + 目标地址(2) + UDS 数据。</summary>
    public static byte[] BuildDiagnosticPayload(ushort sourceAddress, ushort targetAddress, byte[] uds)
    {
        var payload = new byte[4 + uds.Length];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), sourceAddress);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2, 2), targetAddress);
        Buffer.BlockCopy(uds, 0, payload, 4, uds.Length);
        return payload;
    }

    /// <summary>
    /// 路由激活请求负载：源地址(2) + 激活类型(1) + 保留(4) + OEM(4，可省略)。
    /// 带上 OEM 段是常见实现（长度 11），这里默认发 7 字节的保留段版本。
    /// </summary>
    public static byte[] BuildRoutingActivationPayload(ushort sourceAddress, byte activationType)
    {
        var payload = new byte[7];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), sourceAddress);
        payload[2] = activationType;
        // payload[3..7] 保留字节，全 0
        return payload;
    }

    /// <summary>存活检查响应负载：源地址(2)。</summary>
    public static byte[] BuildAliveCheckPayload(ushort sourceAddress)
    {
        var payload = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), sourceAddress);
        return payload;
    }

    /// <summary>
    /// 从缓冲区里切出一帧。数据不足时返回 false（继续等）；
    /// consumed 为已消费的字节数。
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> buffer, out DoIpFrame? frame, out int consumed, out string? error)
    {
        frame = null;
        consumed = 0;
        error = null;

        if (buffer.Length < HeaderSize)
        {
            return false;
        }

        if ((byte)~buffer[0] != buffer[1])
        {
            error = $"协议版本与取反值不匹配（0x{buffer[0]:X2} / 0x{buffer[1]:X2}）";
            consumed = 1;
            return false;
        }

        ushort payloadType = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(2, 2));
        uint length = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4));

        // 单帧上限按 16MB 兜底，防止长度字段异常时无限等待
        if (length > 16 * 1024 * 1024)
        {
            error = $"负载长度异常：{length}";
            consumed = HeaderSize;
            return false;
        }

        int total = HeaderSize + (int)length;

        if (buffer.Length < total)
        {
            return false;
        }

        byte[] payload = buffer.Slice(HeaderSize, (int)length).ToArray();
        byte[] raw = buffer[..total].ToArray();

        frame = new DoIpFrame(buffer[0], payloadType, payload, raw);
        consumed = total;
        return true;
    }

    public static string PayloadTypeName(ushort type) => type switch
    {
        PayloadTypeGenericNack => "通用否定应答",
        PayloadTypeVehicleIdRequest => "车辆识别请求",
        PayloadTypeVehicleIdRequestEid => "车辆识别请求（EID）",
        PayloadTypeVehicleIdRequestVin => "车辆识别请求（VIN）",
        PayloadTypeVehicleAnnouncement => "车辆声明/识别响应",
        PayloadTypeRoutingActivationRequest => "路由激活请求",
        PayloadTypeRoutingActivationResponse => "路由激活响应",
        PayloadTypeAliveCheckRequest => "存活检查请求",
        PayloadTypeAliveCheckResponse => "存活检查响应",
        PayloadTypeEntityStatusRequest => "实体状态请求",
        PayloadTypeEntityStatusResponse => "实体状态响应",
        PayloadTypePowerModeRequest => "电源模式请求",
        PayloadTypePowerModeResponse => "电源模式响应",
        PayloadTypeDiagnostic => "诊断报文",
        PayloadTypeDiagnosticAck => "诊断报文确认",
        PayloadTypeDiagnosticNack => "诊断报文否定确认",
        _ => "未知报文"
    };

    /// <summary>源地址所在位置：诊断类取前 2 字节，路由激活响应取前 2 字节。</summary>
    public static ushort? SourceAddressOf(ushort type, byte[] payload)
    {
        if (payload.Length < 2)
        {
            return null;
        }

        return type switch
        {
            PayloadTypeDiagnostic or PayloadTypeDiagnosticAck or PayloadTypeDiagnosticNack
                => BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(0, 2)),

            PayloadTypeRoutingActivationRequest or PayloadTypeRoutingActivationResponse
                or PayloadTypeAliveCheckResponse or PayloadTypeVehicleAnnouncement
                => BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(0, 2)),

            _ => null
        };
    }

    /// <summary>目标地址：诊断类取第 3–4 字节。</summary>
    public static ushort? TargetAddressOf(ushort type, byte[] payload)
    {
        if (payload.Length < 4)
        {
            return null;
        }

        return type switch
        {
            PayloadTypeDiagnostic or PayloadTypeDiagnosticAck or PayloadTypeDiagnosticNack
                => BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(2, 2)),

            _ => null
        };
    }

    /// <summary>应答码：路由激活响应取第 3 字节，诊断确认取第 5 字节。</summary>
    public static byte? AckCodeOf(ushort type, byte[] payload)
    {
        return type switch
        {
            PayloadTypeRoutingActivationResponse when payload.Length >= 3 => payload[2],
            PayloadTypeDiagnosticAck or PayloadTypeDiagnosticNack when payload.Length >= 5 => payload[4],
            PayloadTypeGenericNack when payload.Length >= 1 => payload[0],
            _ => null
        };
    }

    public static string AckCodeName(ushort type, byte code) => type switch
    {
        PayloadTypeRoutingActivationResponse => code switch
        {
            0x00 => "已同意激活",
            0x01 => "已有其他测试端",
            0x02 => "SA 不匹配",
            0x03 => "SA 已注册",
            0x04 => "缺少认证",
            0x05 => "确认被拒绝",
            0x06 => "不支持的激活类型",
            0x10 => "拒绝：未知源地址",
            0x11 => "拒绝：无可用 socket",
            0x12 => "拒绝：源地址不匹配",
            _ => "未知应答码"
        },

        PayloadTypeDiagnosticAck => code switch
        {
            0x00 => "确认",
            _ => $"确认码 0x{code:X2}"
        },

        PayloadTypeDiagnosticNack => code switch
        {
            0x02 => "无效源地址",
            0x03 => "未知目标地址",
            0x04 => "诊断报文过大",
            0x05 => "内存不足",
            0x06 => "目标不可达",
            0x07 => "传输协议错误",
            _ => $"否定确认码 0x{code:X2}"
        },

        PayloadTypeGenericNack => code switch
        {
            0x00 => "报文头格式错误",
            0x01 => "未知负载类型",
            0x02 => "报文过大",
            0x03 => "内存不足",
            0x04 => "负载长度错误",
            _ => $"否定应答码 0x{code:X2}"
        },

        _ => $"0x{code:X2}"
    };
}
