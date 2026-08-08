namespace PencilBox.Core.Communication;

/// <summary>
/// 串口信息
/// </summary>
public class SerialPortInfo
{
    /// <summary>
    /// 串口名称（如 COM3）
    /// </summary>
    public string PortName { get; set; } = string.Empty;

    /// <summary>
    /// 串口描述
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 设备实例路径
    /// </summary>
    public string? DeviceId { get; set; }

    /// <summary>
    /// 用于 ComboBox 显示的字符串
    /// </summary>
    public string Display => string.IsNullOrEmpty(Description)
        ? PortName
        : $"{PortName} - {Description}";

    public override string ToString() => Display;
}
