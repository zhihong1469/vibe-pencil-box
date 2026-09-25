// <copyright file="KnownPen.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Text.Json.Serialization;
using PencilBox.Core.Communication;

namespace PencilBox.Core.Ble;

/// <summary>
/// 已知（历史出现/使用过）的 SunVote 笔端目录项 —— **笔端 MAC 的统一存储模型**。
///
/// 统一原则（两条来源、一份存储）：
/// - **来源无关**：BLE 扫描（<see cref="PenMacSource.BleScan"/>）与串口 `0x51` 读笔端
///   （<see cref="PenMacSource.SerialRead"/>）目的相同（拿到 MAC），一律汇入本模型；
/// - **显示序为单源**：只持久化 <see cref="MacDisplayHex"/>（显示序 12 hex），
///   其他表示（链路序等）一律现算，避免两处冗余；
/// - **下发给基站零转换**：公司协议 `0x51` 写要求**显示序直传**（冻结基线 R9），
///   故本模型**不再提供链路序**——PC 不做任何字节序转换，转换只在基站内部一处。
///
/// 语义：目标笔端不必当前在空气中——只要拿到过（出现过/读到过）就入库并持久化，
/// 之后即使设备暂不在场也能直接下发（基站拿到 MAC 后，设备一出现即自动建联）。
/// </summary>
public class KnownPen
{
    /// <summary>MAC 显示序 12 位 HEX（如 `C656003A5DF1`，持久化单源）</summary>
    public string MacDisplayHex { get; set; } = string.Empty;

    /// <summary>MAC 来源（BLE 扫描 / 串口读取 / 手工）</summary>
    public PenMacSource Source { get; set; } = PenMacSource.BleScan;

    /// <summary>广播名（可直接来自广播；仅展示用）</summary>
    public string? LocalName { get; set; }

    /// <summary>
    /// 协议 `BT_NAME`（32B 字段的字符串形态）。串口 `0x51` 读到即为笔端权威值
    /// （如 `SunVote-R1:C656003A5DF1`）；为空时由 MAC 现拼（<see cref="EffectiveBtName"/>。
    /// </summary>
    public string? BtName { get; set; }

    /// <summary>最近一次扫描信号强度（dBm；串口来源为 0）</summary>
    public int Rssi { get; set; }

    /// <summary>最近一次扫描是否为配对态广播（名字 '#' 尾）</summary>
    public bool IsPairing { get; set; }

    /// <summary>首次出现时间（UTC）</summary>
    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;

    /// <summary>最近一次出现时间（UTC）</summary>
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    /// <summary>被用于绑定制发的次数</summary>
    public int UseCount { get; set; }

    /// <summary>最近一次绑定制发时间（UTC；null=从未使用）</summary>
    public DateTime? LastUsedUtc { get; set; }

    /// <summary>运行期标记：本次扫描是否在场（非持久化）</summary>
    [JsonIgnore]
    public bool IsPresent { get; set; }

    /// <summary>运行期三级层级（由 KnownPenCatalog.AssignLevels 打标，非持久化）</summary>
    [JsonIgnore]
    public PenUsageLevel Level { get; set; } = PenUsageLevel.Seen;

    // ---- 派生（现算，不持久化）----

    /// <summary>显示序字节（下发用；PC 零转换，原样进 `0x51` 写）</summary>
    [JsonIgnore]
    public byte[] MacDisplayOrder => MacAddress.ParseDisplay(MacDisplayHex);

    /// <summary>显示序文本 `AA:BB:CC:DD:EE:FF`</summary>
    [JsonIgnore]
    public string MacDisplayText => MacAddress.FormatDisplay(MacDisplayOrder);

    /// <summary>下发基站时使用的 `BT_NAME`：优先用读到的存档名，缺省按 MAC 自行拼接</summary>
    [JsonIgnore]
    public string EffectiveBtName =>
        string.IsNullOrWhiteSpace(BtName) ? BleNaming.BuildBtName(MacDisplayOrder) : BtName!;

    /// <summary>运行期展示名（层级徽标）</summary>
    [JsonIgnore]
    public string LevelName => Level.ToDisplayName();

    /// <summary>来源展示名（UI 用）</summary>
    [JsonIgnore]
    public string SourceName => Source.ToDisplayName();

    public override string ToString() => $"[{LevelName}/{SourceName}] {MacDisplayText} {LocalName}";
}
