// <copyright file="PenMacSource.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Ble;

/// <summary>
/// 笔端 MAC 的**来源**（统一存储：无论从哪条路拿到，都汇入同一份 <see cref="KnownPen"/> 目录）。
///
/// 两条来源的目的完全相同——**拿到笔端 MAC**，只是手段不同：
/// - <see cref="BleScan"/>：PC 侧 BLE 扫描广播（拿广播地址；名字尾号仅展示/校验）；
/// - <see cref="SerialRead"/>：PC 串口直连笔端，发公司协议 `0x51` 读配对信息（笔端恒回自身 MAC+NAME）；
/// - <see cref="Manual"/>：人工录入（产测/排障兜底）。
///
/// 下游（下发基站）只认目录里的 **显示序 MAC**，与来源无关——这正是"统一"的意义。
/// </summary>
public enum PenMacSource
{
    /// <summary>BLE 广播扫描</summary>
    BleScan = 0,

    /// <summary>串口 `0x51` 读（工厂直连笔端）</summary>
    SerialRead = 1,

    /// <summary>人工录入</summary>
    Manual = 2
}

/// <summary>来源展示名（UI 用）</summary>
public static class PenMacSourceText
{
    public static string ToDisplayName(this PenMacSource source) => source switch
    {
        PenMacSource.BleScan => "蓝牙扫描",
        PenMacSource.SerialRead => "串口读取",
        PenMacSource.Manual => "手工录入",
        _ => "未知"
    };
}
