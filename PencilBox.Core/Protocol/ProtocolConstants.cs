// <copyright file="ProtocolConstants.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Protocol;

/// <summary>
/// 协议帧边界常量（唯一权威源，三端共享语义）
///
/// 设计依据：
///   - link/contract/discuss/02_统一协议架构讨论.md §4「帧边界宏控」
///   - link/host/usb/01_帧格式与传输约定.md（CMD 帧）
///   - link/host/usb/07_OTA与DFU.md（OTA HDLC 帧）
///
/// 后期改刀原则：
///   - 帧标记 / 协议版本 / 载荷上限只在此定义一处，双端同步改此常量即可编译期发现引用点；
///   - 帧标记是空口/USB 硬契约，改名必须双端同步 + 走版本化（CmdProtocolVersion），而非直接替换；
///   - 各协议族（CMD 0x4A4C / OTA 0x7E）命令语义解耦，仅共享本框架常量。
/// </summary>
public static class ProtocolConstants
{
    // ================= 命令帧（CMD，HID 0xA0 通道）=================
    /// <summary>命令帧头标识高字节 'J'</summary>
    public const byte CmdHeadH = 0x4A;

    /// <summary>命令帧头标识低字节 'L'</summary>
    public const byte CmdHeadL = 0x4C;

    /// <summary>命令帧尾标识</summary>
    public const byte CmdTail = 0xED;

    /// <summary>协议版本（嵌入 Channel 的 bit3-0，当前=0）</summary>
    public const byte CmdProtocolVersion = 0x00;

    /// <summary>
    /// 命令帧最大数据载荷（DL 上限）。
    /// 对齐 link/plan/usb/02_落地架构与物理映射.md D1：
    /// 带 RID 承载（0xA0/0x05 集合）：HID 报告 64B = [RID 1B][数据 63B]，单帧 = 7 + DL + 1，
    /// 故 DL ≤ 63-8 = 55（该承载路线已于 2026-09-05 废弃）；
    /// custom_hid(EP4, 0xFF02) 无 RID：报告 64B 全数据 → DL ≤ 56。
    /// 55 取跨承载安全值，EP4 帧化联调时确认取舍。
    /// </summary>
    public const int MaxCmdDataLength = 55;

    /// <summary>命令帧头长度（HEAD 2 + CHANNEL 1 + RESERVED 1 + LENGTH 2 + CMD 1）</summary>
    public const int CmdHeaderLength = 7;

    /// <summary>命令帧脚长度（TAIL）</summary>
    public const int CmdFooterLength = 1;

    // ================= OTA 帧（HDLC，CDC 通道）=================
    /// <summary>OTA 帧起始标记 SOF</summary>
    public const byte OtaSof = 0x7E;

    /// <summary>OTA 帧结束标记 EOF</summary>
    public const byte OtaEof = 0x7E;

    /// <summary>OTA 帧转义标记 ESC</summary>
    public const byte OtaEsc = 0x7D;

    // ================= HID 报告 ID =================
    /// <summary>命令下行报告 ID（PC→Dongle SET_REPORT，旧承载，路线已废弃仅保留备用）</summary>
    public const byte ReportIdCommandDown = 0xA0;

    /// <summary>命令上行报告 ID（Dongle→PC IN，旧承载，路线已废弃仅保留备用）</summary>
    public const byte ReportIdCommandUp = 0x05;

    // ================= 目标 Dongle 识别特征 =================
    // 本组常量与 link/host/usb/usb_device_config.json（宏定义单源）对照维护，改动须双处同步。
    // ⚠ 承载裁决（2026-09-05）：命令通道沿用 custom_hid(EP4, 0xFF02 无 RID)，
    //   见 agent/dongle/docs/issues/01-上位机命令通道沿用custom_hid(EP4)。
    // ⚠ 产品 VID/PID（2026-09-08，link/issues/0-待处理/03）：已从杰理通用 0x5553/0x4155 改为公司注册 0x2F70/0xEA11。
    //   描述符按 little-endian 写入；主机实读序即 0x2F70/0xEA11（值本身无方向歧义）。
    //   过渡期保留旧值 0x5553/0x4155（旧固件），见 Legacy* / IsTargetVidPid。
    //
    // VID/PID 为弱过滤/显示（同方案/多设备可能共享），唯一性来自 HID 厂商集合特征（UsagePageCustomHid）。

    /// <summary>产品 Dongle 的 USB Vendor ID（公司注册值 0x2F70，little-endian 写入；弱过滤，非唯一标识）</summary>
    public const ushort TargetVendorId = 0x2F70;

    /// <summary>产品 Dongle 的 USB Product ID（公司注册值 0xEA11，little-endian 写入；弱过滤，非唯一标识）</summary>
    public const ushort TargetProductId = 0xEA11;

    /// <summary>旧固件过渡用 VID（杰理 SDK 通用 'SU'=0x5553，已废弃，仅过渡期兜底）</summary>
    public const ushort LegacyVendorId = 0x5553;

    /// <summary>旧固件过渡用 PID（杰理 SDK 通用 'UA'=0x4155，已废弃，仅过渡期兜底）</summary>
    public const ushort LegacyProductId = 0x4155;

    /// <summary>
    /// 是否为已知目标 Dongle 的 VID/PID（产品值或旧固件过渡值）。仅弱过滤，配合 0xFF02 主特征判定。
    /// </summary>
    public static bool IsTargetVidPid(ushort vid, ushort pid) =>
        (vid == TargetVendorId && pid == TargetProductId) ||
        (vid == LegacyVendorId && pid == LegacyProductId);

    /// <summary>
    /// custom_hid 命令通道的 Usage Page（EP4 独立厂商 HID，无 Report ID）。
    /// 对齐 agent/dongle 议题01：上位机命令通道沿用 custom_hid(EP4)。
    /// </summary>
    public const ushort UsagePageCustomHid = 0xFF02;

    /// <summary>目标 Dongle 的制造商字符串（descriptor.c MANUFACTURE_STR）</summary>
    public const string TargetManufacturer = "SunVote Technology";

    /// <summary>目标 Dongle 的产品字符串（descriptor.c product_string）</summary>
    public const string TargetProductName = "USB Composite Device";
}
