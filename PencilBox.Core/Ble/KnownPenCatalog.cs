// <copyright file="KnownPenCatalog.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Communication;

namespace PencilBox.Core.Ble;

/// <summary>
/// 已知笔端目录的纯逻辑（无平台依赖，可单测）：
/// - <see cref="Merge"/>：把一次 BLE 扫描结果并入目录（按显示序 MAC 去重，更新在场/名字/RSSI/配对态，新设备记首次时间）。
/// - <see cref="Upsert"/>：把**单条**已知笔端并入目录（供串口 `0x51` 读等"非广播"来源复用同一份存储）。
/// - <see cref="AssignLevels"/>：按"上次使用 &gt; 使用过 &gt; 出现过"打三级层级，并按层级+在场+信号排序（下拉框顺序）。
/// </summary>
public static class KnownPenCatalog
{
    /// <summary>
    /// 合并扫描结果到目录（就地更新/追加），并返回最新目录列表。
    /// </summary>
    public static List<KnownPen> Merge(IReadOnlyList<KnownPen> existing, IEnumerable<BleDeviceInfo> scans)
    {
        var now = DateTime.UtcNow;
        var list = new List<KnownPen>(existing);

        foreach (var scan in scans)
        {
            if (!scan.IsSunVotePen)
                continue;

            var key = scan.MacDisplayText;
            var found = list.FirstOrDefault(p =>
                string.Equals(p.MacDisplayText, key, StringComparison.OrdinalIgnoreCase));

            if (found != null)
            {
                found.LocalName = scan.LocalName ?? found.LocalName;
                found.Rssi = scan.Rssi;
                found.IsPairing = scan.IsPairingMode;
                found.LastSeenUtc = now;
                found.IsPresent = true;
            }
            else
            {
                list.Add(new KnownPen
                {
                    MacDisplayHex = scan.MacDisplayText.Replace(":", ""),
                    Source = PenMacSource.BleScan,
                    LocalName = scan.LocalName,
                    Rssi = scan.Rssi,
                    IsPairing = scan.IsPairingMode,
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                    IsPresent = true
                });
            }
        }

        return list;
    }

    /// <summary>
    /// 把**单条**已知笔端并入目录（按显示序 MAC 去重）。
    /// 用途：**串口 `0x51` 读笔端**等非广播来源——与 BLE 扫描写入同一份目录，实现"两条来源、一份存储"。
    /// 已存在则仅更新来源/存档名/时间（保留使用次数与首次时间），不破坏既有历史。
    /// </summary>
    /// <param name="existing">现有目录</param>
    /// <param name="incoming">新拿到的笔端（至少要有 <see cref="KnownPen.MacDisplayHex"/>）</param>
    /// <returns>并发并后的目录列表（就地更新 existing 中的项）</returns>
    public static List<KnownPen> Upsert(IReadOnlyList<KnownPen> existing, KnownPen incoming)
    {
        var now = DateTime.UtcNow;
        var list = new List<KnownPen>(existing);

        var found = list.FirstOrDefault(p =>
            string.Equals(p.MacDisplayText, incoming.MacDisplayText, StringComparison.OrdinalIgnoreCase));

        if (found == null)
        {
            incoming.LastSeenUtc = now;
            list.Add(incoming);
            return list;
        }

        // 已有：更新来源与协议名（串口读到的 BT_NAME 是权威存档值），保留使用统计
        found.Source = incoming.Source;
        if (!string.IsNullOrWhiteSpace(incoming.BtName))
            found.BtName = incoming.BtName;
        if (!string.IsNullOrWhiteSpace(incoming.LocalName))
            found.LocalName = incoming.LocalName;
        found.Rssi = incoming.Rssi;
        found.LastSeenUtc = now;

        return list;
    }

    /// <summary>
    /// 给目录打三级层级并排序（降序）：上次使用 &gt; 使用过 &gt; 出现过；层内按在场 &gt; RSSI 降序。
    /// </summary>
    public static List<KnownPen> AssignLevels(IReadOnlyList<KnownPen> pens)
    {
        var list = pens.ToList();

        // 计算"最近一次使用"时间（用于区分 上次使用/使用过）
        var lastUsedMax = list
            .Where(p => p.LastUsedUtc.HasValue)
            .Select(p => p.LastUsedUtc!.Value)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();

        foreach (var p in list)
        {
            p.Level = p.LastUsedUtc.HasValue
                ? (p.LastUsedUtc!.Value == lastUsedMax ? PenUsageLevel.LastUsed : PenUsageLevel.Used)
                : PenUsageLevel.Seen;
        }

        return list
            .OrderByDescending(p => p.Level)
            .ThenByDescending(p => p.IsPresent)
            .ThenByDescending(p => p.Rssi)
            .ToList();
    }
}
