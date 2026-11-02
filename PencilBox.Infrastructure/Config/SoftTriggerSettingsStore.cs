// <copyright file="SoftTriggerSettingsStore.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PencilBox.Core.SoftTrigger;

namespace PencilBox.Infrastructure.Config;

/// <summary>
/// <see cref="ISoftTriggerSettings"/> 文件实现：`&lt;exe 同级&gt;/configs/softtrigger-settings.json` 单文件单值。
/// 缺文件/损坏 → 默认 800（不炸引擎）；写侧夹取 0~2000。读无缓存——引擎每次派发实读，
/// CLI 与 GUI 两个进程改任意一边，下一次触发即生效（跨进程新鲜度优先，文件极小读耗可忽略）。
/// </summary>
public sealed class SoftTriggerSettingsStore : ISoftTriggerSettings
{
    /// <summary>默认松开等待 ms（v2.3 实测定标：覆盖绝大多数长按意图，又不至于明显拖慢连击）。</summary>
    public const int DefaultReleaseWaitMs = 800;

    /// <summary>合法域上限 ms。</summary>
    public const int MaxReleaseWaitMs = 2000;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger<SoftTriggerSettingsStore> _logger;
    private readonly object _gate = new();

    public SoftTriggerSettingsStore(ILogger<SoftTriggerSettingsStore> logger, string? baseDirectory = null)
    {
        _path = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "configs", "softtrigger-settings.json");
        _logger = logger;
    }

    public int ReleaseWaitMs
    {
        get
        {
            lock (_gate)
            {
                try
                {
                    if (!File.Exists(_path))
                        return DefaultReleaseWaitMs;
                    var dto = JsonSerializer.Deserialize<SettingsDto>(File.ReadAllText(_path), JsonOpts);
                    return Math.Clamp(dto?.ReleaseWaitMs ?? DefaultReleaseWaitMs, 0, MaxReleaseWaitMs);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "软触发设置不可读，用默认值：{Path}", _path);
                    return DefaultReleaseWaitMs;
                }
            }
        }
    }

    public void SetReleaseWaitMs(int ms)
    {
        var clamped = Math.Clamp(ms, 0, MaxReleaseWaitMs);
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(new SettingsDto { ReleaseWaitMs = clamped }, JsonOpts));
        }
    }

    private sealed class SettingsDto
    {
        public int ReleaseWaitMs { get; set; } = DefaultReleaseWaitMs;
    }
}
