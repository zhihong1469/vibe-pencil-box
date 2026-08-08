namespace PencilBox.Core.Protocol;

/// <summary>
/// 命令 ID 定义（对应 Dongle 固件中的 APP_CMD_* 枚举）
/// </summary>
public enum CommandId : byte
{
    /// <summary>
    /// RCSP 协议数据透传
    /// </summary>
    RcspData = 0x00,

    /// <summary>
    /// 获取 Dongle 信息
    /// </summary>
    GetDongleMessage = 0x01,

    /// <summary>
    /// 获取已连接设备列表
    /// </summary>
    GetConnectDevice = 0x02,

    /// <summary>
    /// 操作成功响应
    /// </summary>
    ReturnSucc = 0x03,

    /// <summary>
    /// 请求重连设备
    /// </summary>
    ReconnectDevice = 0x04,

    /// <summary>
    /// 断开设备连接
    /// </summary>
    DisconnectDevice = 0x05,

    /// <summary>
    /// 认证标志位
    /// </summary>
    AuthFlag = 0x06,

    /// <summary>
    /// 自定义命令（厂商扩展）
    /// </summary>
    Custom = 0xFF
}

/// <summary>
/// 通信通道定义
/// </summary>
public enum HidChannel : byte
{
    /// <summary>
    /// PC → Dongle 命令通道
    /// </summary>
    PcToDongle = 0x00,

    /// <summary>
    /// Dongle → PC 响应通道
    /// </summary>
    DongleToPc = 0x10,

    /// <summary>
    /// PC → USB 透传通道
    /// </summary>
    PcToUsb = 0x20,

    /// <summary>
    /// 远端 OTA 透传通道 1
    /// </summary>
    Remote1 = 0x30,

    /// <summary>
    /// 远端 OTA 透传通道 2
    /// </summary>
    Remote2 = 0x40,

    /// <summary>
    /// 远端 OTA 透传通道 3
    /// </summary>
    Remote3 = 0x50,

    /// <summary>
    /// 远端 OTA 透传通道 4
    /// </summary>
    Remote4 = 0x60,

    /// <summary>
    /// 远端 OTA 透传通道 5
    /// </summary>
    Remote5 = 0x70,

    /// <summary>
    /// 远端 OTA 透传通道 6
    /// </summary>
    Remote6 = 0x80
}

/// <summary>
/// 命令方向
/// </summary>
public enum CommandDirection
{
    /// <summary>
    /// PC 发送到 Dongle
    /// </summary>
    PcToDongle,

    /// <summary>
    /// Dongle 发送到 PC
    /// </summary>
    DongleToPc,

    /// <summary>
    /// 双向
    /// </summary>
    Bidirectional
}

/// <summary>
/// 命令元数据特性
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public class CommandMetadataAttribute : Attribute
{
    /// <summary>
    /// 命令方向
    /// </summary>
    public CommandDirection Direction { get; }

    /// <summary>
    /// 命令描述
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// 期望的响应命令 ID
    /// </summary>
    public CommandId? ExpectedResponse { get; set; }

    public CommandMetadataAttribute(CommandDirection direction, string description)
    {
        Direction = direction;
        Description = description;
    }
}
