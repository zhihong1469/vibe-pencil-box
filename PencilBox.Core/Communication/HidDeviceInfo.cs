namespace PencilBox.Core.Communication;

/// <summary>
/// HID 设备信息
/// </summary>
public class HidDeviceInfo
{
    /// <summary>
    /// Windows 设备路径（唯一标识）
    /// </summary>
    public string DevicePath { get; set; } = string.Empty;

    /// <summary>
    /// USB Vendor ID
    /// </summary>
    public ushort VendorId { get; set; }

    /// <summary>
    /// USB Product ID
    /// </summary>
    public ushort ProductId { get; set; }

    /// <summary>
    /// 产品名称
    /// </summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>
    /// 制造商名称
    /// </summary>
    public string Manufacturer { get; set; } = string.Empty;

    /// <summary>
    /// HID Input Report 长度
    /// </summary>
    public int InputReportLength { get; set; }

    /// <summary>
    /// HID Output Report 长度
    /// </summary>
    public int OutputReportLength { get; set; }

    /// <summary>
    /// HID Feature Report 长度
    /// </summary>
    public int FeatureReportLength { get; set; }

    /// <summary>
    /// HID Usage Page
    /// </summary>
    public ushort UsagePage { get; set; }

    /// <summary>
    /// HID Usage
    /// </summary>
    public ushort Usage { get; set; }

    /// <summary>
    /// 序列号
    /// </summary>
    public string SerialNumber { get; set; } = string.Empty;

    /// <summary>
    /// 版本号（BCD）
    /// </summary>
    public ushort ReleaseNumber { get; set; }

    /// <summary>
    /// 是否为 HID 设备
    /// </summary>
    public bool IsHidDevice { get; set; } = true;

    /// <summary>
    /// 是否为音频设备
    /// </summary>
    public bool IsAudioDevice { get; set; }

    /// <summary>
    /// 是否为本产品设备（VID/PID 命中产品值 0x2F70/0xEA11 或过渡期旧值 0x5553/0x4155）。
    /// 设备层：用于枚举"只关注自己的设备"，作为浅黄底标识。
    /// </summary>
    public bool IsOurDevice { get; set; }

    /// <summary>
    /// 是否为 EP4 命令通道接口（custom_hid Usage Page 0xFF02 / 无 Report ID）。
    /// 接口层：真正用于通信（读电量/协议）的接口，标 ★；同一复合设备下唯一。
    /// 端点处理（EP4 OUT 0x04 / IN 0x84 上下行）在传输层内部完成。
    /// </summary>
    public bool IsCommandChannel { get; set; }

    /// <summary>
    /// 是否为目标 Dongle（本产品设备身上的厂商命令集合特征：custom_hid 0xFF02 或 0xA0/0x05）。
    /// 该字段聚合"设备+接口"语义，供兼容使用；新 UI/CLI 请用 IsOurDevice / IsCommandChannel。
    /// </summary>
    public bool IsTargetDongle { get; set; }

    /// <summary>
    /// 接口号：从设备路径解析 `mi_XX`（十六进制）得到，-1 表示未知。
    /// 对应复合设备里的接口序号（如 mi_02 → 接口 2）。
    /// </summary>
    public int InterfaceNumber => ParseHexField(DevicePath, "mi_");

    /// <summary>
    /// 集合号：从设备路径解析 `col_XX`（十六进制）得到，-1 表示无集合（单接口单集合）。
    /// 同一接口下有多个 HID 集合时用于区分（如 键盘/鼠标/消费键集合）。
    /// </summary>
    public int CollectionNumber => ParseHexField(DevicePath, "col");

    /// <summary>
    /// 接口类型名（可读）：区分"这是什么样的接口"。
    /// 上位机 HID 扫描下的类型：custom_hid 命令通道 / 备用 0xA0·0x05 / 键盘 / 普通 HID 输入集合。
    /// </summary>
    public string InterfaceTypeName =>
        IsCommandChannel ? "CustomHID命令通道(端点:0x04收发/0x84收)"
        : (IsTargetDongle && IsOurDevice) ? "备用通道(0xA0/0x05)"
        : (DevicePath.Contains("kbd", StringComparison.OrdinalIgnoreCase) || IsKeyboardLike) ? "键盘(HID)"
        : "HID输入集合";

    /// <summary>
    /// 接口展示标签：接口号 + 集合号 + 接口名，保证同一次扫描内每条唯一可区分。
    /// 例：`接口2 · CustomHID命令通道(端点:0x04收发/0x84收)`、`接口3·col1 · 键盘(HID)`。
    /// </summary>
    public string InterfaceLabel
    {
        get
        {
            var num = InterfaceNumber >= 0 ? $"接口{InterfaceNumber}" : "接口?";
            if (CollectionNumber >= 0)
                num += $"·col{CollectionNumber}";
            return $"{num} · {InterfaceTypeName}";
        }
    }

    private bool IsKeyboardLike =>
        InputReportLength is 9 or 8 && OutputReportLength is 2 or 1;

    private static int ParseHexField(string path, string tag)
    {
        if (string.IsNullOrEmpty(path))
            return -1;
        int idx = path.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
        if (idx < 0 || idx + tag.Length + 2 > path.Length)
            return -1;
        return int.TryParse(path.Substring(idx + tag.Length, 2),
            System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int val) ? val : -1;
    }

    public override string ToString()
    {
        return $"USB\\VID_{VendorId:X4}&PID_{ProductId:X4} - {ProductName}";
    }
}
