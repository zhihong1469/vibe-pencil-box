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
/// 保存前过值域校验（<see cref="SoftTriggerKeys.Validate"/>——不合规不落盘）；损坏文件告警跳过（不炸整表）。
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
                    var def = JsonSerializer.Deserialize<SoftTriggerDefinition>(File.ReadAllText(path), JsonOpts);
                    if (def != null && !string.IsNullOrWhiteSpace(def.Name))
                        defs.Add(def);
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
                return JsonSerializer.Deserialize<SoftTriggerDefinition>(File.ReadAllText(path), JsonOpts);
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
        SoftTriggerKeys.Validate(definition);   // 值域分离硬约束：不合规不落盘

        lock (_gate)
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(PathOf(definition.Name), JsonSerializer.Serialize(definition, JsonOpts));
        }
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
