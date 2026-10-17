// <copyright file="IKeymapConfigStore.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Models;

namespace PencilBox.Core.Services;

/// <summary>
/// 命名 Keymap 配置本地存储（pc-issues/04 提案 B）。
/// 目录：`&lt;exe 同级&gt;/configs/keymap/`，一配置一 JSON（便携，随 exe 走）；
/// 内置"出厂默认"只读项（<see cref="KeymapFactoryDefault"/>）；
/// `%LocalAppData%` 的旧单缓存（`config/keymap.json`）首启迁移为"本地缓存"配置。
/// </summary>
public interface IKeymapConfigStore
{
    /// <summary>出厂默认配置名（只读：拒绝改名/删除/覆盖保存）</summary>
    string FactoryName { get; }

    /// <summary>全部配置名（出厂默认恒最前，其余按名排序）</summary>
    IReadOnlyList<string> ListNames();

    /// <summary>加载指定配置；不存在或损坏返回 null</summary>
    KeymapConfigDocument? Load(string name);

    /// <summary>保存配置（新建或覆盖；出厂默认名拒绝）。switchEntries=null = 不携带切换表（读时回落出厂真值）</summary>
    void Save(string name, IEnumerable<KeymapEntry> entries, IEnumerable<KeymapSwitchEntry>? switchEntries = null);

    /// <summary>改名（出厂默认拒绝；目标已存在抛异常）</summary>
    void Rename(string oldName, string newName);

    /// <summary>删除配置（出厂默认拒绝）；删的是当前配置则回落出厂默认</summary>
    void Delete(string name);

    /// <summary>当前选中配置名（未记录或已失效 = 出厂默认）</summary>
    string GetCurrentName();

    /// <summary>记录当前选中配置名</summary>
    void SetCurrentName(string name);

    /// <summary>初始化：确保目录与出厂默认文件在位 + 旧缓存迁移（幂等；宿主启动调一次）</summary>
    void Initialize();
}
