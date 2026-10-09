// <copyright file="KeymapConfigDocument.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Models;

/// <summary>
/// 一份命名 Keymap 配置——<see cref="Services.IKeymapConfigStore"/> 的存储单元
/// （pc-issues/04 提案 B：`&lt;exe 同级&gt;/configs/keymap/&lt;配置名&gt;.json`，一配置一文件）。
/// </summary>
public sealed class KeymapConfigDocument
{
    /// <summary>配置名（=文件名去扩展名；出厂默认名为 <see cref="Services.IKeymapConfigStore.FactoryName"/>）</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>创建时间</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>映射条目（索引即接收器表槽位）</summary>
    public List<KeymapEntry> Entries { get; set; } = new();
}
