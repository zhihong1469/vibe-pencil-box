// <copyright file="SubCommands.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Protocol;

/// <summary>
/// CUSTOM（CMD=0xFF）下的 Sub-CMD 消息 ID 分配。
/// 权威：link/dongle/usb/design/01_协议/02_命令族全集.md（v3.1）§3；只增不改，编号冻结。
/// 注意：Sub 0x22~0x2F 为预留（本地配对已在 CMD 层 PAIR_* 0xA1~0xA4，勿在此段重复分配）。
/// </summary>
public enum PcSubCommand : byte
{
    // ========== 音频 / DSP 参数（0x01~0x04，本地） ==========
    FirUpdate = 0x01,
    AgcUpdate = 0x02,
    VadUpdate = 0x03,
    FullParamsUpdate = 0x04,

    // ========== 快捷键管理（0x10~0x17）——已退役（2026-10-17） ==========
    // 老"0xA0 阶段 6"PC↔Dongle Keymap 读写方案：固件从未实现，PC 侧 KeymapService /
    // KeymapProtocol / IKeymapService 已随同清退。后续由 F5 轨 0x55 KEYMAP_CONFIG 承接
    // （pc-issues 0x55 提案）；本段编号退役不回收、不复用，保持族内冻结。

    // ========== 设备控制（0x20~0x2F，穿透） ==========
    // 本地配对走 CMD 层 PAIR_*（0xA1~0xA4）；0x22~0x2F 预留勿用（含未来 PAIR_REPLACE 等规划）
    SetVoiceState = 0x20,
    GetVoiceState = 0x21,

    // ========== 参数配置（0x30~0x3F，穿透/本地） ==========
    SetMouseSensitivity = 0x30,
    GetMouseSensitivity = 0x31,

    // ========== 状态查询（0x40~0x4F） ==========
    QueryDeviceInfo = 0x40,     // 穿透：→[status][型号2][fw_ver4][proto_ver1]
    QueryBattery = 0x41,        // 穿透：→[status][%]
    GetPencilState = 0x42,      // 本地（DG 缓存秒回）：→[status][chg][batt][conn][rssi]（chg=byte2 充电枚举副本，V1.3）
    GetOtaSupport = 0x43,       // 本地：→[status][support][ota_ch]

    // ========== 上行事件通知（0x50~0x5F，DG→PC） ==========
    EvtBatteryReport = 0x50,    // [%][charging]（变化才发）
    EvtVoiceState = 0x51,       // [state]
    EvtConnectionState = 0x52,  // [state][MAC6]
    EvtKeyMonitor = 0x53,       // [key_id][action]（监听模式）
}

/// <summary>
/// Keymap 条目的事件类型（event_type）
/// </summary>
public enum KeyEventType : byte
{
    Click = 0x01,
    DoubleClick = 0x02,
    LongPress = 0x03,
    LongRelease = 0x04,
    LongSpecial = 0x05,
}

/// <summary>
/// Keymap 条目的动作类型（action_type）
/// </summary>
public enum KeyActionType : byte
{
    None = 0x00,
    Keyboard = 0x01,
    MouseButton = 0x02,
    Consumer = 0x03,
    Special = 0x04,
    AppNotify = 0x05,
}

/// <summary>
/// 笔端充电状态枚举（byte2 bit1-0，V1.3：原"工作模式"语义已退役，
/// 识别/扩音档由上位机凭 F13~F16 事件自行推导；0x42 本地缓存与 0x53 同源）
/// </summary>
public enum PencilChargeState : byte
{
    NotCharging = 0x00,
    Charging = 0x01,
    Full = 0x02,
}

/// <summary>
/// 笔端连接状态
/// </summary>
public enum PencilConnectionState : byte
{
    Disconnected = 0x00,
    Connected = 0x01,
}
