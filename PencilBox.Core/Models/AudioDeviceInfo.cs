namespace PencilBox.Core.Models;

/// <summary>
/// 音频输入设备信息
/// </summary>
public class AudioDeviceInfo
{
    /// <summary>
    /// 设备唯一标识（NAudio MMDevice ID）
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// 设备友好名称
    /// </summary>
    public string FriendlyName { get; set; } = string.Empty;

    /// <summary>
    /// 是否为 USB 设备
    /// </summary>
    public bool IsUsbDevice { get; set; }

    /// <summary>
    /// USB Vendor ID（仅 USB 设备有效）
    /// </summary>
    public ushort? VendorId { get; set; }

    /// <summary>
    /// USB Product ID（仅 USB 设备有效）
    /// </summary>
    public ushort? ProductId { get; set; }

    /// <summary>
    /// USB 设备路径/位置信息
    /// </summary>
    public string? UsbDevicePath { get; set; }

    /// <summary>
    /// 用于 WPF 显示的字符串
    /// </summary>
    public string DisplayName
    {
        get
        {
            if (IsUsbDevice && VendorId.HasValue && ProductId.HasValue)
            {
                return $"[USB] {FriendlyName} (VID_{VendorId.Value:X4}, PID_{ProductId.Value:X4})";
            }
            return FriendlyName;
        }
    }

    public override string ToString() => DisplayName;
}
