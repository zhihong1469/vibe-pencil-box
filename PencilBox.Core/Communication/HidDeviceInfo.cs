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

    public override string ToString()
    {
        return $"USB\\VID_{VendorId:X4}&PID_{ProductId:X4} - {ProductName}";
    }
}
