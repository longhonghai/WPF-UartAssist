namespace WPF_UartAssist.Services;

/// <summary>UDS 服务参数：ByteCount 为 0 表示变长 HEX，>0 表示固定字节数。</summary>
public sealed record UdsParamSpec(string Label, int ByteCount, string DefaultValue, string Tip);

/// <summary>一条 UDS 服务定义。SubFunction 仅用于显示（如 0x19/0x02）。</summary>
public sealed record UdsService(byte Sid, string Name, string? SubFunction, IReadOnlyList<UdsParamSpec> Params)
{
    /// <summary>下拉里显示的文字：22 ReadDataByIdentifier。</summary>
    public string Display => SubFunction is null
        ? $"{Sid:X2}  {Name}"
        : $"{Sid:X2}/{SubFunction}  {Name}";
}

/// <summary>服务分类，对应菜单里的分组。</summary>
public sealed record UdsCategory(string Name, IReadOnlyList<UdsService> Services)
{
    public override string ToString() => Name;
}

/// <summary>UDS（ISO 14229）服务目录、组帧与响应解析。</summary>
public static class Uds
{
    private static UdsParamSpec P(string label, int bytes, string defaultValue, string tip)
        => new(label, bytes, defaultValue, tip);

    /// <summary>截图里列出的全部服务。</summary>
    public static readonly IReadOnlyList<UdsCategory> Categories =
    [
        new("诊断通信管理（SID 0x10-0x3E）",
        [
            new(0x10, "DiagnosticSessionControl", null,
            [
                P("子功能", 1, "01", "01 默认会话 / 02 编程会话 / 03 扩展会话；bit7=抑制肯定响应"),
            ]),
            new(0x11, "EcuReset", null,
            [
                P("复位类型", 1, "01", "01 硬复位 / 02 钥匙关开 / 03 软复位"),
            ]),
            new(0x27, "SecurityAccess", null,
            [
                P("子功能", 1, "01", "奇数=请求种子，偶数=发送密钥（如 01/02）"),
                P("安全数据", 0, "", "请求种子时留空；发送密钥时填 HEX"),
            ]),
            new(0x3E, "TesterPresent", null,
            [
                P("子功能", 1, "00", "00 需要肯定响应 / 80 抑制肯定响应"),
            ]),
            new(0x28, "CommunicationControl", null,
            [
                P("控制类型", 1, "00", "00 启用收发 / 01 仅收 / 02 仅发 / 03 禁止收发"),
                P("通信类型", 1, "01", "01 普通报文 / 02 网络管理报文 / 03 两者"),
            ]),
            new(0x85, "ControlDtcSetting", null,
            [
                P("设置类型", 1, "01", "01 打开 DTC 记录 / 02 关闭 DTC 记录"),
            ]),
            new(0x29, "Authentication", null,
            [
                P("子功能", 1, "00", "00 取消认证 / 01 校验证书 / 02 发送挑战 …"),
                P("认证数据", 0, "", "按子功能可选"),
            ]),
        ]),

        new("数据传输（SID 0x22-0x3D）",
        [
            new(0x22, "ReadDataByIdentifier", null,
            [
                P("DID", 2, "F190", "数据标识符，如 F190 读 VIN"),
            ]),
            new(0x24, "ReadScalingDataByIdentifier", null,
            [
                P("DID", 2, "F190", "要读换算信息的数据标识符"),
            ]),
            new(0x2E, "WriteDataByIdentifier", null,
            [
                P("DID", 2, "F190", "数据标识符"),
                P("写入数据", 0, "", "HEX，长度需与 DID 定义一致"),
            ]),
            new(0x23, "ReadMemoryByAddress", null,
            [
                P("地址长度格式", 1, "14", "高 4 位=地址字节数，低 4 位=长度字节数"),
                P("地址+长度", 0, "00001000 00000010", "按上面的格式拼：地址 + 要读的字节数"),
            ]),
            new(0x3D, "WriteMemoryByAddress", null,
            [
                P("地址长度格式", 1, "14", "高 4 位=地址字节数，低 4 位=长度字节数"),
                P("地址+长度", 0, "00001000 00000002", "地址 + 要写的字节数"),
                P("写入数据", 0, "", "HEX"),
            ]),
            new(0x2A, "ReadDataByPeriodicIdentifier", null,
            [
                P("传输模式", 1, "01", "01 慢速 / 02 中速 / 03 快速 / 04 停止"),
                P("周期标识", 0, "F1", "一个或多个周期数据标识符（每字节一个）"),
            ]),
            new(0x2C, "DynamicallyDefineDataIdentifier", null,
            [
                P("子功能", 1, "01", "01 按标识符定义 / 02 按内存地址定义 / 03 清除"),
                P("定义内容", 0, "F190 0001 F191 0002", "子功能 03 时填要清除的动态 DID"),
            ]),
            new(0x2C, "ClearDynamicallyDefinedDataIdentifier", "03",
            [
                P("子功能", 1, "03", "固定 03：清除动态定义"),
                P("动态 DID", 2, "F200", "要清除的动态数据标识符"),
            ]),
        ]),

        new("输入输出控制（SID 0x2F）",
        [
            new(0x2F, "InputOutputControlByIdentifier", null,
            [
                P("DID", 2, "F190", "要控制的输入输出标识符"),
                P("控制参数", 1, "00", "00 由控制状态决定 / 01 恢复默认 / 02 冻结当前状态 / 03 短期调整"),
                P("控制状态", 0, "", "可选，HEX"),
            ]),
        ]),

        new("故障码管理（SID 0x14/0x19）",
        [
            new(0x14, "ClearDiagnosticInformation", null,
            [
                P("DTC 组", 3, "FFFFFF", "FFFFFF 表示全部"),
            ]),
            new(0x19, "ReadDtcByStatusMask", "02",
            [
                P("子功能", 1, "02", "固定 02：按状态掩码读 DTC"),
                P("状态掩码", 1, "FF", "08 已确认 / 09 测试失败 / FF 全部"),
            ]),
            new(0x19, "ReadDtcCountByStatusMask", "01",
            [
                P("子功能", 1, "01", "固定 01：统计符合条件的 DTC 数量"),
                P("状态掩码", 1, "FF", "08 已确认 / 09 测试失败 / FF 全部"),
            ]),
            new(0x19, "ReadDtcSnapshotIdentification", "03",
            [
                P("子功能", 1, "03", "固定 03：读 DTC 快照标识"),
                P("DTC 记录", 0, "", "可选，HEX"),
            ]),
            new(0x19, "ReadSupportedDtc", "0A",
            [
                P("子功能", 1, "0A", "固定 0A：读 ECU 支持的 DTC"),
            ]),
        ]),

        new("例程控制（SID 0x31）",
        [
            new(0x31, "RoutineControl", null,
            [
                P("子功能", 1, "01", "01 启动例程 / 02 停止例程 / 03 查询结果"),
                P("例程 ID", 2, "0203", "RID"),
                P("例程参数", 0, "", "可选，HEX"),
            ]),
        ]),

        new("上传/下载（SID 0x34-0x38）",
        [
            new(0x34, "RequestDownload", null,
            [
                P("数据格式", 1, "00", "bit0-3=压缩方式，bit4-7=加密方式，00 为不压缩不加密"),
                P("地址长度格式", 1, "44", "高 4 位=地址字节数，低 4 位=大小字节数"),
                P("地址+大小", 0, "00001000 00001000", "内存地址 + 数据字节数"),
            ]),
            new(0x35, "RequestUpload", null,
            [
                P("数据格式", 1, "00", "同上"),
                P("地址长度格式", 1, "44", "同上"),
                P("地址+大小", 0, "00001000 00001000", "内存地址 + 数据字节数"),
            ]),
            new(0x38, "RequestFileTransfer", null,
            [
                P("模式", 1, "01", "01 添加文件 / 02 删除 / 03 替换 / 04 读文件 / 05 读目录"),
                P("文件路径与格式", 0, "00 2F 74 65 73 74 2E 62 69 6E", "数据格式 + 文件路径长度与路径"),
            ]),
        ]),

        new("原始报文",
        [
            new(0x00, "RawRequest", null,
            [
                P("原始 UDS", 0, "22 F1 90", "直接发送这段 HEX，不加 SID"),
            ]),
        ]),
    ];

    /// <summary>按服务定义拼 UDS 请求（RawRequest 直接返回原始字节）。</summary>
    public static bool TryBuild(UdsService service, IReadOnlyList<string> values, out byte[] payload, out string error)
    {
        payload = [];
        error = string.Empty;

        var bytes = new List<byte>();

        if (service.Sid != 0x00)
        {
            bytes.Add(service.Sid);
        }

        for (int i = 0; i < service.Params.Count; i++)
        {
            UdsParamSpec spec = service.Params[i];
            string text = i < values.Count ? values[i] : string.Empty;

            if (string.IsNullOrWhiteSpace(text))
            {
                if (spec.ByteCount > 0)
                {
                    error = $"{spec.Label} 不能为空";
                    return false;
                }

                continue;
            }

            if (!HexConverter.TryParse(text, out byte[] part, out _))
            {
                error = $"{spec.Label} 不是合法 HEX";
                return false;
            }

            if (spec.ByteCount > 0 && part.Length != spec.ByteCount)
            {
                error = $"{spec.Label} 应为 {spec.ByteCount} 字节（当前 {part.Length} 字节）";
                return false;
            }

            bytes.AddRange(part);
        }

        if (bytes.Count == 0)
        {
            error = "报文为空";
            return false;
        }

        payload = bytes.ToArray();
        return true;
    }

    /// <summary>SID 中文名。</summary>
    public static string SidName(byte sid) => sid switch
    {
        0x10 => "诊断会话控制",
        0x11 => "ECU 复位",
        0x14 => "清除诊断信息",
        0x19 => "读取 DTC 信息",
        0x22 => "按标识符读数据",
        0x23 => "按地址读内存",
        0x24 => "读换算信息",
        0x27 => "安全访问",
        0x28 => "通信控制",
        0x29 => "认证",
        0x2A => "周期读数据",
        0x2C => "动态定义数据标识符",
        0x2E => "按标识符写数据",
        0x2F => "输入输出控制",
        0x31 => "例程控制",
        0x34 => "请求下载",
        0x35 => "请求上传",
        0x37 => "传输数据",
        0x38 => "请求文件传输",
        0x3D => "按地址写内存",
        0x3E => "诊断仪在线",
        0x85 => "DTC 设置控制",
        _ => "未知服务"
    };

    /// <summary>否定响应码（NRC）含义。</summary>
    public static string NrcName(byte nrc) => nrc switch
    {
        0x10 => "一般拒绝",
        0x11 => "服务不支持",
        0x12 => "子功能不支持",
        0x13 => "报文长度或格式错误",
        0x14 => "响应过长",
        0x21 => "忙，请重试",
        0x22 => "条件不满足",
        0x24 => "请求序列错误",
        0x25 => "子网无此请求",
        0x26 => "条件不正确",
        0x31 => "请求超出范围",
        0x33 => "安全访问被拒绝",
        0x35 => "密钥无效",
        0x36 => "超过尝试次数",
        0x37 => "延时未到",
        0x70 => "上传下载未接受",
        0x71 => "传输数据暂停",
        0x72 => "编程失败",
        0x73 => "块序号错误",
        0x78 => "已接受，响应挂起",
        0x7E => "当前会话不支持该子功能",
        0x7F => "当前会话不支持该服务",
        _ => "未定义"
    };

    /// <summary>把收到的 UDS 响应翻译成一行说明。</summary>
    public static string DescribeResponse(byte[] data)
    {
        if (data.Length == 0)
        {
            return "空响应";
        }

        if (data[0] == 0x7F)
        {
            if (data.Length < 3)
            {
                return "否定响应（长度不足）";
            }

            return $"否定响应：{data[1]:X2} {SidName(data[1])} → NRC {data[2]:X2}（{NrcName(data[2])}）";
        }

        if (data[0] >= 0x40)
        {
            byte request = (byte)(data[0] - 0x40);
            string name = SidName(request);

            // 22 的肯定响应：62 + DID(2) + 数据
            if (request == 0x22 && data.Length >= 3)
            {
                string payload = data.Length > 3 ? Convert.ToHexString(data[3..]) : "(无数据)";
                return $"肯定响应：{name}，DID {data[1]:X2}{data[2]:X2} = {payload}";
            }

            // 27 的肯定响应：67 + 子功能 + 种子/密钥
            if (request == 0x27 && data.Length >= 2)
            {
                string payload = data.Length > 2 ? Convert.ToHexString(data[2..]) : string.Empty;
                return $"肯定响应：{name}，子功能 {data[1]:X2}，{(data[1] % 2 == 1 ? "种子" : "已通过")} {payload}".TrimEnd();
            }

            return $"肯定响应：{name}（{Convert.ToHexString(data)}）";
        }

        return $"UDS 数据：{Convert.ToHexString(data)}";
    }
}
