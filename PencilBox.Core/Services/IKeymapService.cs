// <copyright file="IKeymapService.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Models;

namespace PencilBox.Core.Services;

/// <summary>
/// Keymap 快捷键管理服务接口
/// 负责从 Dongle 实时读取 Keymap、修改条目、提交/回滚，并维护本地缓存（掉电不丢失）。
/// 对齐 link/host/usb/04_快捷键管理.md
/// </summary>
public interface IKeymapService
{
    /// <summary>Keymap 变化事件（读取/修改后触发）</summary>
    event EventHandler<IReadOnlyList<KeymapEntry>>? KeymapChanged;

    /// <summary>读取 Keymap 元信息（版本/条目数/模式/上限）</summary>
    Task<KeymapInfo?> GetKeymapInfoAsync(CancellationToken ct = default);

    /// <summary>从 Dongle 实时读取全部 Keymap 条目（分页）</summary>
    Task<IReadOnlyList<KeymapEntry>> ReadKeymapAsync(CancellationToken ct = default);

    /// <summary>修改单条映射（写 L2 缓存）</summary>
    Task<bool> SetKeyEntryAsync(byte index, KeymapEntry entry, CancellationToken ct = default);

    /// <summary>删除单条（恢复默认）</summary>
    Task<bool> DeleteKeyEntryAsync(byte index, CancellationToken ct = default);

    /// <summary>全量恢复出厂默认</summary>
    Task<bool> ResetKeymapAsync(CancellationToken ct = default);

    /// <summary>提交修改（L2 缓存 → 运行表 + NVDS）</summary>
    Task<bool> CommitAsync(CancellationToken ct = default);

    /// <summary>丢弃 L2 修改</summary>
    Task<bool> RollbackAsync(CancellationToken ct = default);

    /// <summary>当前缓存的 Keymap（可能来自本地文件，尚未连设备时）</summary>
    IReadOnlyList<KeymapEntry> CachedKeymap { get; }

    /// <summary>从本地文件加载缓存（掉电不丢失）</summary>
    Task LoadCacheAsync(CancellationToken ct = default);

    /// <summary>保存到本地文件缓存</summary>
    Task SaveCacheAsync(IEnumerable<KeymapEntry> entries, CancellationToken ct = default);
}
