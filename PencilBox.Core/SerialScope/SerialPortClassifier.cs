namespace PencilBox.Core.SerialScope;

/// <summary>串口角色（05 §3.2：`SerialPortEnumerator` 的"类型识别"）</summary>
public enum SerialPortRole
{
    /// <summary>认不出来</summary>
    Unknown,

    /// <summary>USB 转串口桥（CH340/CH341/CP210x/FTDI…）—— 我们设备的**主用**形态</summary>
    UsbBridge,

    /// <summary>蓝牙虚拟串口（与设备无直接关系，多半是别的蓝牙外设）</summary>
    Bluetooth,

    /// <summary>调试器/烧录器虚拟口（`JLVirtualJtagSerial` / ST-Link / CMSIS-DAP）</summary>
    JtagVirtual
}

/// <summary>
/// 串口角色识别（纯逻辑）。
///
/// 取值口径照抄参考实现 `serial-chat/core/serial_reader.py::auto_detect_port` 的判据序，
/// 目的有二（05 §3.5「端口选择」）：
/// 1. **别选错**：把蓝牙口 / 调试口标出来，减少"选了口却没数据"的迷惑；
/// 2. **别误会**：`JLVirtualJtagSerial`（Dongle 日志口 COM6 @1M）**是可以读日志的**
///    （坑点 §七实测可用），但它与烧录工具**抢口** ⇒ 只提示、不禁止（05 §5.3）。
/// </summary>
public static class SerialPortClassifier
{
    /// <summary>调试器/烧录器虚拟口特征（**能读日志**，但与烧录工具冲突）</summary>
    private static readonly string[] JtagMarkers =
        { "JTAG", "JLVIRTUAL", "JL_VIRTUAL", "STLINK", "ST-LINK", "CMSIS" };

    /// <summary>USB 转串口桥特征</summary>
    private static readonly string[] BridgeMarkers =
        { "CH340", "CH341", "CH343", "CP210", "FT232", "FTDI", "SILICON LABS", "USB-SERIAL", "USB SERIAL", "PROLIFIC" };

    /// <summary>蓝牙虚拟串口特征</summary>
    private static readonly string[] BluetoothMarkers = { "BLUETOOTH", "蓝牙" };

    /// <summary>按端口描述判定角色</summary>
    public static SerialPortRole Classify(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return SerialPortRole.Unknown;

        var upper = description.ToUpperInvariant();

        if (JtagMarkers.Any(m => upper.Contains(m, StringComparison.Ordinal)))
            return SerialPortRole.JtagVirtual;
        if (BridgeMarkers.Any(m => upper.Contains(m, StringComparison.Ordinal)))
            return SerialPortRole.UsbBridge;
        if (BluetoothMarkers.Any(m => description.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return SerialPortRole.Bluetooth;

        return SerialPortRole.Unknown;
    }

    /// <summary>中文标注（`serial list` 的"类型"列）</summary>
    public static string Text(SerialPortRole role) => role switch
    {
        SerialPortRole.UsbBridge => "USB转串口",
        SerialPortRole.Bluetooth => "蓝牙串口",
        SerialPortRole.JtagVirtual => "调试器虚拟口",
        _ => "未知"
    };

    /// <summary>
    /// "更像我们设备"的偏好排序（越小越优先）—— 自动挑口与界面默认选中**共用这一处**。
    ///
    /// ⚠️ 刻意**与参考实现不同**：`serial-chat` 把"调试器虚拟口"排到最后（它面向通用场景），
    /// 但**我们 Dongle 的日志口本身就是 `JLVirtualJtagSerial`（COM6 @1M）** —— 照它排，
    /// 自动挑口会跳过我们的口去选蓝牙口，等于永远挑不中（坑点速查 §七）。
    /// 本产品顺序：USB 转串口桥（笔端侧） &gt; 调试器虚拟口（基站侧） &gt; 未知 &gt; 蓝牙（几无可能是我方设备）。
    /// </summary>
    public static int Preference(SerialPortRole role) => role switch
    {
        SerialPortRole.UsbBridge => 0,
        SerialPortRole.JtagVirtual => 1,
        SerialPortRole.Unknown => 2,
        _ => 3
    };

    /// <summary>选中该口时是否需要**前置提示**（只对"有坑"的角色提示，正常口不打扰）</summary>
    public static string? SelectionWarning(SerialPortRole role) => role switch
    {
        SerialPortRole.JtagVirtual => "调试器虚拟口：可读日志，但可能与烧录/调试工具抢口",
        SerialPortRole.Bluetooth => "蓝牙虚拟串口：通常不是我们的设备，确认后再抓",
        _ => null
    };
}
