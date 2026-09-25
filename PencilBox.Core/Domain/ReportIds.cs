// <copyright file="ReportIds.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Domain;

/// <summary>
/// USB HID Report ID 常量（对应 Dongle 固件中的 RID_* 定义）
/// </summary>
public static class ReportIds
{
    /// <summary>
    /// 键盘 HID 报告 ID
    /// </summary>
    public const byte Keyboard = 0x01;

    /// <summary>
    /// Consumer 控制报告 ID（播放/暂停/静音等）
    /// </summary>
    public const byte Consumer = 0x02;

    /// <summary>
    /// 鼠标报告 ID
    /// </summary>
    public const byte Mouse = 0x03;

    /// <summary>
    /// 音频数据报告 ID（ADPCM 流）
    /// </summary>
    public const byte Audio = 0x04;

    /// <summary>
    /// 音频控制报告 ID
    /// </summary>
    public const byte AudioControl = 0x05;

    /// <summary>
    /// 自定义命令报告 ID（PC → Dongle 命令通道）
    /// </summary>
    public const byte CustomCommand = 0xA0;

    /// <summary>
    /// 标准 HID 报告长度
    /// </summary>
    public const int StandardReportLength = 64;

    /// <summary>
    /// 获取所有可用的 Report ID
    /// </summary>
    public static readonly byte[] All = { Keyboard, Consumer, Mouse, Audio, AudioControl, CustomCommand };
}

/// <summary>
/// USB HID Usage Page 常量
/// </summary>
public static class UsagePages
{
    /// <summary>
    /// 通用桌面控制
    /// </summary>
    public const ushort GenericDesktop = 0x01;

    /// <summary>
    /// 键盘
    /// </summary>
    public const ushort Keyboard = 0x07;

    /// <summary>
    /// 鼠标
    /// </summary>
    public const ushort Mouse = 0x09;

    /// <summary>
    /// 消费者控制
    /// </summary>
    public const ushort Consumer = 0x0C;

    /// <summary>
    /// 厂商自定义
    /// </summary>
    public const ushort VendorDefined = 0xFF00;
}
