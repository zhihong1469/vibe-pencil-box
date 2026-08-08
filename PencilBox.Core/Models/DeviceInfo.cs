namespace PencilBox.Core.Models;

/// <summary>
/// Dongle 设备信息
/// </summary>
public class DeviceInfo
{
    /// <summary>
    /// 设备类型（0x02 = Dongle）
    /// </summary>
    public byte DeviceType { get; set; }

    /// <summary>
    /// 设备 ID（高字节）
    /// </summary>
    public ushort DeviceId { get; set; }

    /// <summary>
    /// 厂商名称（4 字节）
    /// </summary>
    public string Manufacturer { get; set; } = string.Empty;

    /// <summary>
    /// 固件版本号
    /// </summary>
    public byte Version { get; set; }

    /// <summary>
    /// USB 厂商 ID
    /// </summary>
    public ushort VendorId { get; set; }

    /// <summary>
    /// USB 产品 ID
    /// </summary>
    public ushort ProductId { get; set; }

    /// <summary>
    /// 产品名称
    /// </summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>
    /// HID Input Report 长度
    /// </summary>
    public int InputReportLength { get; set; }

    /// <summary>
    /// HID Output Report 长度
    /// </summary>
    public int OutputReportLength { get; set; }

    /// <summary>
    /// 设备路径（Windows 内部标识）
    /// </summary>
    public string DevicePath { get; set; } = string.Empty;
}

/// <summary>
/// 已连接的蓝牙设备
/// </summary>
public class ConnectedDevice
{
    /// <summary>
    /// 设备索引
    /// </summary>
    public byte Index { get; set; }

    /// <summary>
    /// 设备 MAC 地址
    /// </summary>
    public string MacAddress { get; set; } = string.Empty;

    /// <summary>
    /// 设备名称
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 连接状态
    /// </summary>
    public ConnectionState State { get; set; }

    /// <summary>
    /// 信号强度（RSSI）
    /// </summary>
    public int Rssi { get; set; }
}

/// <summary>
/// 连接状态枚举
/// </summary>
public enum ConnectionState
{
    /// <summary>
    /// 已连接
    /// </summary>
    Connected,

    /// <summary>
    /// 已断开
    /// </summary>
    Disconnected,

    /// <summary>
    /// 正在连接
    /// </summary>
    Connecting,

    /// <summary>
    /// 正在断开
    /// </summary>
    Disconnecting
}
