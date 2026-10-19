// <copyright file="SoftTriggerStore.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PencilBox.Core.SoftTrigger;

namespace PencilBox.Infrastructure.Config;

/// <summary>
/// <see cref="ISoftTriggerStore"/> 文件实现：`&lt;exe 同级&gt;/configs/softtriggers/&lt;名&gt;.json` 一条一文件。
/// 保存前过单定义值域校验（<see cref="SoftTriggerKeys.Validate"/>——不合规不落盘）+
/// **动态预留全局律**（<see cref="SoftTriggerKeys.ValidateGlobal"/>：模拟保存后的启用集，触发∩动作=∅，
/// 冲突点名拒绝——防回环 v2）；装载时把 v1 旧字段 StepIntervalMs 摊给未显式设延时的步（<see cref="Migrate"/>）。
/// 损坏文件告警跳过（不炸整表）。
/// </summary>
public sealed class SoftTriggerStore : ISoftTriggerStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _dir;
    private readonly ILogger<SoftTriggerStore> _logger;
    private readonly object _gate = new();

    public SoftTriggerStore(ILogger<SoftTriggerStore> logger, string? baseDirectory = null)
    {
        _dir = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "configs", "softtriggers");
        _logger = logger;
    }

    public IReadOnlyList<SoftTriggerDefinition> List()
    {
        lock (_gate)
        {
            if (!Directory.Exists(_dir))
                return [];

            var defs = new List<SoftTriggerDefinition>();
            foreach (var path in Directory.GetFiles(_dir, "*.json"))
            {
                try
                {
                    var def = JsonSerializer.Deserialize<SoftTriggerDefinition>(ReadMigrated(path), JsonOpts);
                    if (def != null && !string.IsNullOrWhiteSpace(def.Name))
                        defs.Add(Migrate(def));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "软触发定义不可读，跳过：{Path}", path);
                }
            }
            return defs.OrderBy(d => d.Name, StringComparer.Ordinal).ToList();
        }
    }

    public SoftTriggerDefinition? Load(string name)
    {
        lock (_gate)
        {
            var path = PathOf(name);
            if (!File.Exists(path))
                return null;
            try
            {
                return Migrate(JsonSerializer.Deserialize<SoftTriggerDefinition>(ReadMigrated(path), JsonOpts)!);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "软触发定义损坏：{Path}", path);
                return null;
            }
        }
    }

    public void Save(SoftTriggerDefinition definition)
    {
        SoftTriggerKeys.Validate(definition);   // 单定义值域硬约束：不合规不落盘

        lock (_gate)
        {
            // 动态预留全局律：模拟"保存后启用集"（现有启用 − 同名旧条 + 本条），触发∩动作=∅ 才落盘
            var enabledAfterSave = List()
                .Where(d => !string.Equals(d.Name, definition.Name, StringComparison.Ordinal) && d.Enabled)
                .ToList();
            if (definition.Enabled)
                enabledAfterSave.Add(definition);
            SoftTriggerKeys.ValidateGlobal(enabledAfterSave);

            Directory.CreateDirectory(_dir);
            File.WriteAllText(PathOf(definition.Name), JsonSerializer.Serialize(definition, JsonOpts));
        }
    }

    /// <summary>
    /// 读文件文本 + 旧字段名迁移：v2.3 步后延时(DelayMsAfter)→步前延时(DelayMsBefore)。
    /// 旧"步后"语义下用户设延时几乎都是想要"本步前等待"，纯改名（值随步走）即合用；
    /// 大小写不敏感替换，反序列化侧本来就不区分。
    /// </summary>
    private static string ReadMigrated(string path) =>
        File.ReadAllText(path).Replace("DelayMsAfter", "DelayMsBefore", StringComparison.OrdinalIgnoreCase);

    /// <summary>v1 → v2 归一：旧全局步间隔摊给未显式设延时的步（显式 @延时的步保留），字段清零退役。</summary>
    private static SoftTriggerDefinition Migrate(SoftTriggerDefinition def)
    {
        if (def.StepIntervalMs > 0)
        {
            for (var i = 0; i < def.Steps.Count; i++)
            {
                if (def.Steps[i].DelayMsBefore == 0)
                    def.Steps[i] = def.Steps[i] with { DelayMsBefore = def.StepIntervalMs };
            }
            def.StepIntervalMs = 0;
        }
        return def;
    }

    public void Delete(string name)
    {
        lock (_gate)
        {
            var path = PathOf(name);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    public void SetEnabled(string name, bool enabled)
    {
        var def = Load(name) ?? throw new KeyNotFoundException($"软触发不存在：{name}");
        def.Enabled = enabled;
        Save(def);
    }

    private string PathOf(string name) => Path.Combine(_dir, $"{name}.json");
}
