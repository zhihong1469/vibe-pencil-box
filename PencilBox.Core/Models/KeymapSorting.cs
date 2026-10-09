// <copyright file="KeymapSorting.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Models;

/// <summary>
/// Keymap 条目的**规范排序**（2026-10-08 用户定版，pc-issues/04）：
/// 键序 K1→K6（key_id 升序）→ 同键按事件序（CLICK→DOUBLE→LONG_PRESS→LONG_RELEASE→LONG_SPECIAL）
/// → 组合键（0xF0~0xFF）垫底。
/// 存储与显示共用此序——配置文件里的顺序即表格显示顺序；查表端按 (key_id, event) 扫描，
/// 对顺序不敏感，重排零功能影响。
/// 注意：<see cref="KeymapFactoryDefault.Entries"/> 常量保持**固件原序**（字节级对拍锚点），
/// 排序只发生在存储落盘与加载时（<see cref="Infrastructure.Config.KeymapConfigStore"/> 归一化）。
/// </summary>
public static class KeymapSorting
{
    /// <summary>按规范排序返回新列表（稳定排序）</summary>
    public static List<KeymapEntry> Canonical(IEnumerable<KeymapEntry> entries) =>
        entries
            .OrderBy(e => e.KeyId >= 0xF0 ? 1 : 0)   // 组合键垫底
            .ThenBy(e => e.KeyId)                    // K1→K6
            .ThenBy(e => (byte)e.EventType)          // 事件序
            .ToList();
}
