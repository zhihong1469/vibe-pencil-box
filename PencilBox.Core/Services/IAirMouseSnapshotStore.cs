// <copyright file="IAirMouseSnapshotStore.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Protocol.Company;

namespace PencilBox.Core.Services;

/// <summary>
/// 空鼠调参**方案快照**一条：全表参数值（旧固件 6 参 / 扩表 11 参，写回时只读/控制型跳过）+ 存档元数据（03 卷 §6；仅本地 JSON，不下笔端）。
/// </summary>
/// <param name="Name">快照名（建议 A/B 互切对比手感）</param>
/// <param name="SavedAt">保存时间（本地）</param>
/// <param name="Note">备注（随快照存档）</param>
/// <param name="Firmware">保存时的笔固件版本（可空）</param>
/// <param name="Values">参数读值（通常 6 项全表）</param>
/// <param name="Checklist">试手感四项清单（单击准/双击原地/瞄准不飘/快甩到位），null=未勾过</param>
public sealed record AirMouseSnapshot(
    string Name, DateTime SavedAt, string? Note, string? Firmware,
    IReadOnlyList<TuningParamReading> Values, IReadOnlyList<bool>? Checklist = null);

/// <summary>
/// 空鼠方案快照存储（`&lt;exe 同级&gt;/configs/airmouse/&lt;名&gt;.json`，同 keymap 配置存储模式）。
/// CLI `company param snapshot` 与 Desktop 空鼠工作台共用——快照跨会话存活、A/B 可由脚本驱动。
/// </summary>
public interface IAirMouseSnapshotStore
{
    /// <summary>全部快照名（文件名序）</summary>
    IReadOnlyList<string> ListNames();

    /// <summary>保存（同名覆盖）</summary>
    void Save(AirMouseSnapshot snapshot);

    /// <summary>按名读取（不存在返回 null）</summary>
    AirMouseSnapshot? Load(string name);

    /// <summary>按名删除（存在与否都返回成功语义：true=文件已不在或已删）</summary>
    bool Delete(string name);
}
