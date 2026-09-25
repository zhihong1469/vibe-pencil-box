// <copyright file="Commands.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Protocol;

/// <summary>
/// 命令 ID 定义（对应 Dongle 固件 srv/pc_cmd 的 CMD 层，4A4C 帧）。
/// 权威：link/dongle/usb/design/01_协议/02_命令族全集.md（v3.1）§2；只增不改，编号冻结。
/// </summary>
public enum CommandId : byte
{
    /// <summary>
    /// RCSP_DATA：RCSP 透传（RCSP OTA 网关时代；本产品不使用）
    /// </summary>
    RcspData = 0x00,

    /// <summary>
    /// GET_DONGLE_MESSAGE：获取 Dongle 信息；应答 型号(2)+固件版本(4)+协议版本(1)+能力位(1)
    /// </summary>
    GetDongleMessage = 0x01,

    /// <summary>
    /// GET_CONNECT_DEVICE：获取已连接设备列表；应答 [connected][(ch)(state)…]
    /// </summary>
    GetConnectDevice = 0x02,

    /// <summary>
    /// RETURN_SUCC：通用成功应答（RCSP 门控，二期收编）
    /// </summary>
    ReturnSucc = 0x03,

    /// <summary>
    /// RECONNECT_DEVICE：请求重连设备 [ch][MAC6][方式]
    /// </summary>
    ReconnectDevice = 0x04,

    /// <summary>
    /// DISCONNECT_DEVICE：断开设备连接 [ch]
    /// </summary>
    DisconnectDevice = 0x05,

    /// <summary>
    /// AUTH_FLAG：认证标志位 [ch][flag]
    /// </summary>
    AuthFlag = 0x06,

    // ===== 量产配对族 PAIR_*（CMD 层本地配对 · 冻结段，v3.1 登记保留）=====
    // 权威 02 §2：0xA1~0xA4 只承载下列 4 条语义，不扩展；新命令一律进 0xFF Sub。

    /// <summary>PAIR_GET_STATE：查询配对状态；应答 [paired][connected][mode][MAC6][name_len][name…]</summary>
    PairGetState = 0xA1,

    /// <summary>PAIR_NAME：进入 60s 名称配对窗（匹配 "SunVote-R1#"）→[status]</summary>
    PairName = 0xA2,

    /// <summary>PAIR_CLEAR_TARGET：清 INFO3（置 FF）→[status]</summary>
    PairClearTarget = 0xA3,

    /// <summary>PAIR_SET_TARGET：写 INFO3（[MAC6 链路序]）→[status]</summary>
    PairSetTarget = 0xA4,

    /// <summary>
    /// CUSTOM（0xFF）：厂商扩展，Data[0]=Sub-CMD（业务语义，见 PcSubCommand）
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
