namespace WPF_UartAssist.Models;

/// <summary>日志通道，与顶部页签一一对应（索引即页签索引）。</summary>
public enum LogChannel
{
    /// <summary>串口页签。</summary>
    Serial = 0,

    /// <summary>TCP / UDP 页签。</summary>
    Network = 1,

    /// <summary>S7 西门子页签。</summary>
    S7 = 2,

    /// <summary>MQTT 页签。</summary>
    MQTT = 3,

    /// <summary>OPC UA 页签。</summary>
    OpcUa = 4,

    /// <summary>DoIP 页签。</summary>
    DoIp = 5
}

/// <summary>日志条目的类别，决定显示颜色。</summary>
public enum LogKind
{
    /// <summary>接收到的数据（绿色）。</summary>
    Receive,

    /// <summary>发出的数据（蓝色）。</summary>
    Send,

    /// <summary>系统提示，如打开/关闭串口（灰色）。</summary>
    System
}
