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
///
/// **跨进程跟随**（10-19 E2E 实测缺口）：CLI 与 GUI 双进程共用本文件——本进程缓存内存态后，
/// 另一进程的 feature on/off 不会自动可见。双保险：① FileSystemWatcher + 250ms 防抖快路径；
/// ② 2s 兜底轮询（FSW 有丢事件劣迹：网络盘/高负载/沙箱重定向均不可信）。
/// 两路都汇入 ReloadFromDisk 差异比对——只对真变化发 Changed；本进程自写后重读无差异→无事件，天然防回环。
/// </summary>
public sealed class FeatureToggleService : IFeatureToggleService, IDisposable
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
    private readonly FileSystemWatcher? _watcher;
    private System.Threading.Timer? _reloadDebounce;
    private readonly System.Threading.Timer? _pollTimer;

    public FeatureToggleService(ILogger<FeatureToggleService> logger, string? baseDirectory = null)
    {
        _path = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "configs", "features.json");
        _logger = logger;

        try
        {
            var dir = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(dir);
            _watcher = new FileSystemWatcher(dir, "features.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            // Created 也要收：首次写入（文件原先不存在）只发 Created 不发 Changed——恰是另一进程第一次 off 的场景
            _watcher.Created += OnConfigFileChanged;
            _watcher.Changed += OnConfigFileChanged;
            _watcher.Deleted += OnConfigFileChanged;   // 删文件=回落目录默认值，语义上也是变更
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "features.json 监视挂载失败（快路径不可用，轮询兜底不受影响）");
        }

        // 兜底轮询：FSW 丢事件/挂载失败的场合 2s 内仍跟上（重读+差异比对幂等，空转无副作用）
        _pollTimer = new System.Threading.Timer(
            _ => ReloadFromDisk(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

        // 预热基线快照：跨进程 diff 的"旧值"来源——不预热的话外部首次写入会被当成初始状态而非变更
        LoadOverrides();
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _reloadDebounce?.Dispose();
        _pollTimer?.Dispose();
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

    // FSW 回调：写者常连发多起（同一次 WriteAllText 也可能两起）——防抖合并成一次重读
    private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        lock (_gate)
        {
            _reloadDebounce ??= new System.Threading.Timer(_ => ReloadFromDisk(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            _reloadDebounce.Change(250, System.Threading.Timeout.Infinite);
        }
    }

    /// <summary>外部改文件 → 强制重读 → 与内存表比对，差异键逐个补发 Changed（GUI 页签显隐/引擎停机订阅在此跟上）。</summary>
    private void ReloadFromDisk()
    {
        List<(FeatureDescriptor Descriptor, bool Enabled)> changes = [];
        lock (_gate)
        {
            var old = LoadOverrides();
            _overrides = null;                       // 强制下一次真读盘
            var fresh = LoadOverrides();

            foreach (var descriptor in FeatureCatalog.All)
            {
                var was = old.TryGetValue(descriptor.Id, out var o) ? o : descriptor.DefaultEnabled;
                var now = fresh.TryGetValue(descriptor.Id, out var n) ? n : descriptor.DefaultEnabled;
                if (was != now)
                    changes.Add((descriptor, now));
            }
        }

        foreach (var (descriptor, enabled) in changes)
        {
            _logger.LogInformation("features.json 外部变更：{Id} → {Enabled}", descriptor.Id, enabled);
            Changed?.Invoke(this, new FeatureToggleChangedEventArgs(descriptor, enabled));
        }
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
