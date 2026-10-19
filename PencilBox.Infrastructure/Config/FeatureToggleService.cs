// <copyright file="FeatureToggleService.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PencilBox.Core.Features;

namespace PencilBox.Infrastructure.Config;

/// <summary>
/// <see cref="IFeatureToggleService"/> 文件实现（14 号架构提案）：
/// `<exe 同级>/configs/features.json` 单文件持久化，键=功能 id、值=bool；
/// 文件缺失/键缺失 → <see cref="FeatureCatalog"/> 默认值；文件损坏 → 告警回落默认（下次 SetEnabled 重写覆盖）；
/// 未知键忽略（目录收缩后旧键不残留报错，前向兼容）。
/// </summary>
public sealed class FeatureToggleService : IFeatureToggleService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // 用户可能手改本文件：camelCase/PascalCase 键都收（默认大小写敏感会静默丢键）
        PropertyNameCaseInsensitive = true,
    };

    private sealed class PersistModel
    {
        public Dictionary<string, bool> Features { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly string _path;
    private readonly ILogger<FeatureToggleService> _logger;
    private readonly object _gate = new();
    private Dictionary<string, bool>? _overrides;

    public FeatureToggleService(ILogger<FeatureToggleService> logger, string? baseDirectory = null)
    {
        _path = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "configs", "features.json");
        _logger = logger;
    }

    public event EventHandler<FeatureToggleChangedEventArgs>? Changed;

    public IReadOnlyList<FeatureDescriptor> Describe() => FeatureCatalog.All;

    public FeatureDescriptor GetDescriptor(string featureId) =>
        FeatureCatalog.Find(featureId)
        ?? throw new KeyNotFoundException($"未知功能 id：{featureId}（可用：{string.Join("、", FeatureCatalog.All.Select(f => f.Id))}）");

    public bool IsEnabled(string featureId)
    {
        var descriptor = GetDescriptor(featureId);
        lock (_gate)
        {
            return LoadOverrides().TryGetValue(descriptor.Id, out var enabled) ? enabled : descriptor.DefaultEnabled;
        }
    }

    public void SetEnabled(string featureId, bool enabled)
    {
        var descriptor = GetDescriptor(featureId);

        lock (_gate)
        {
            var overrides = LoadOverrides();
            overrides[descriptor.Id] = enabled;
            WriteOverrides(overrides);
        }

        _logger.LogInformation("功能开关已更新：{Id} → {Enabled}", descriptor.Id, enabled);
        Changed?.Invoke(this, new FeatureToggleChangedEventArgs(descriptor, enabled));
    }

    /// <summary>取覆写表（懒加载；损坏回落空表）。调用方须持 <see cref="_gate"/>。</summary>
    private Dictionary<string, bool> LoadOverrides()
    {
        if (_overrides != null)
            return _overrides;

        _overrides = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(_path))
                return _overrides;

            var model = JsonSerializer.Deserialize<PersistModel>(File.ReadAllText(_path), JsonOpts) ??
                        throw new InvalidDataException("空内容");
            foreach (var (key, value) in model.Features)
            {
                // 未知键忽略（目录收缩前向兼容）；键大小写归一到目录 id
                var descriptor = FeatureCatalog.Find(key);
                if (descriptor != null)
                    _overrides[descriptor.Id] = value;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "features.json 不可读，回落目录默认值（文件：{Path}）", _path);
            _overrides = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }
        return _overrides;
    }

    private void WriteOverrides(Dictionary<string, bool> overrides)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var json = JsonSerializer.Serialize(new PersistModel { Features = overrides }, JsonOpts);
        File.WriteAllText(_path, json);
    }
}
