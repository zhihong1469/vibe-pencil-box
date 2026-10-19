// <copyright file="ISoftTriggerStore.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.SoftTrigger;

/// <summary>
/// 软触发定义本地存储（动作库键盘族 13 号）：`&lt;exe 同级&gt;/configs/softtriggers/&lt;名&gt;.json` 一条一文件。
/// 保存时过 <see cref="SoftTriggerKeys.Validate"/>（值域分离是硬约束，不合规不落盘）。
/// </summary>
public interface ISoftTriggerStore
{
    /// <summary>全部定义（按名排序）。</summary>
    IReadOnlyList<SoftTriggerDefinition> List();

    /// <summary>按名加载；不存在或损坏返回 null。</summary>
    SoftTriggerDefinition? Load(string name);

    /// <summary>新建或覆盖（名字为键；保存前校验值域）。</summary>
    void Save(SoftTriggerDefinition definition);

    /// <summary>删除。</summary>
    void Delete(string name);

    /// <summary>只翻启用位（载入-改-存）。</summary>
    void SetEnabled(string name, bool enabled);
}
