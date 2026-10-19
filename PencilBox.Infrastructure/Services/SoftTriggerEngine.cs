// <copyright file="SoftTriggerEngine.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PencilBox.Core.Features;
using PencilBox.Core.Models;
using PencilBox.Core.Services;
using PencilBox.Core.SoftTrigger;

namespace PencilBox.Infrastructure.Services;

/// <summary>
/// <see cref="ISoftTriggerEngine"/> 实现（动作库键盘族 13 号，2026-10-19；v2 同日）。
/// 观测腿=共享全局键盘钩子（工作状态监听共用一条 WH_KEYBOARD_LL）；发腿=<see cref="ISyntheticKeyboard"/>（SendInput）。
/// v2：触发键全键盘池 ± 修饰——钩子收到 VK 后按定义的触发修饰**精确匹配**（要求的都按着、没要求的都没按，
/// GetAsyncKeyState 实时查询，防打字误触）；同触发键多条按 Priority 降序派发；
/// 防回环=动态预留全局律（触发∩动作=∅）在装载处兜底：贪心装载，与已装载集冲突的定义拒载并记事件。
/// </summary>
public sealed class SoftTriggerEngine : ISoftTriggerEngine, IDisposable
{
    /// <summary>功能总开关 id（FeatureCatalog.softtrigger）。</summary>
    public const string FeatureId = "softtrigger";

    private readonly ISoftTriggerStore _store;
    private readonly IGlobalKeyboardHook _hook;
    private readonly ISyntheticKeyboard _sender;
    private readonly IFeatureToggleService _toggles;
    private readonly ILogger<SoftTriggerEngine> _logger;

    private readonly object _gate = new();
    private readonly Queue<SoftTriggerEvent> _events = new();
    private readonly ConcurrentDictionary<string, Timer> _toggleTimers = new();
    private Dictionary<byte, List<SoftTriggerDefinition>> _byTrigger = new();   // 触发键 usage → 启用定义（Priority 降序）

    private bool _subscribed;

    public SoftTriggerEngine(
        ISoftTriggerStore store,
        IGlobalKeyboardHook hook,
        ISyntheticKeyboard sender,
        IFeatureToggleService toggles,
        ILogger<SoftTriggerEngine> logger)
    {
        _store = store;
        _hook = hook;
        _sender = sender;
        _toggles = toggles;
        _logger = logger;

        // 总开关联动：任何一侧（CLI/GUI）关掉 softtrigger → 引擎即停（14 号：关闭=运行中引擎即停）
        _toggles.Changed += OnToggleChanged;
    }

    public event EventHandler<SoftTriggerEvent>? EventLogged;

    public bool IsRunning => Volatile.Read(ref _subscribed);

    public void Start()
    {
        if (!_toggles.IsEnabled(FeatureId))
            throw new InvalidOperationException("软触发功能已停用（feature off softtrigger）——先执行 pencilbox feature on softtrigger");

        lock (_gate)
        {
            if (_subscribed)
                return;
            ReloadCore();
            _hook.KeyDown += OnKeyDown;
            _hook.Start();          // 共享钩子幂等；工作状态监听可能已拉起
            _subscribed = true;
        }
        Log(SoftTriggerEvent.KindTriggerSeen, "引擎", "引擎已启动（钩子观测腿就位，触发池=全键盘±修饰）");
        _logger.LogInformation("软触发引擎已启动");
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_subscribed)
                return;
            _hook.KeyDown -= OnKeyDown;
            _subscribed = false;
        }
        StopAllToggles();
        Log("引擎", "引擎", "引擎已停止（总开关关闭或宿主叫停；连发全部终止）");
        _logger.LogInformation("软触发引擎已停止");
    }

    public void Reload()
    {
        lock (_gate)
        {
            if (!_subscribed)
                return;
            ReloadCore();
        }
        _logger.LogInformation("软触发定义已重载");
    }

    public void Fire(string name)
    {
        var def = _store.Load(name) ?? throw new KeyNotFoundException($"软触发不存在：{name}");
        if (!def.Enabled)
            throw new InvalidOperationException($"软触发「{name}」处于停用状态（softtrigger off {name}）");
        Log("试射", name, "手动试射（不经钩子，直接回放）");
        Replay(def);
    }

    public IReadOnlyList<SoftTriggerEvent> RecentEvents(int count)
    {
        lock (_gate)
        {
            return _events.Take(Math.Max(0, count)).ToList();
        }
    }

    // ===== 内部 =====

    /// <summary>
    /// 装载启用定义（贪心：按 Priority 降序逐条装载，每条与已装载集跑动态预留律，
    /// 冲突者拒载并记事件——存储层已校验过，这里只兜手改文件的底）。调用方持 _gate。
    /// </summary>
    private void ReloadCore()
    {
        var byTrigger = new Dictionary<byte, List<SoftTriggerDefinition>>();
        var loaded = new List<SoftTriggerDefinition>();

        foreach (var def in _store.List().Where(d => d.Enabled).OrderByDescending(d => d.Priority))
        {
            try
            {
                SoftTriggerKeys.Validate(def);
                SoftTriggerKeys.ValidateGlobal(loaded.Concat([def]).ToList());
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "软触发「{Name}」拒载", def.Name);
                Log(SoftTriggerEvent.KindError, def.Name, $"装载被拒：{ex.Message}");
                continue;
            }
            if (!byTrigger.TryGetValue(def.TriggerUsageId, out var list))
                byTrigger[def.TriggerUsageId] = list = [];
            list.Add(def);   // 已按 Priority 降序遍历，追加即有序
            loaded.Add(def);
        }
        _byTrigger = byTrigger;
    }

    /// <summary>钩子线程回调：usage 查表 + 触发修饰精确匹配（防打字误触）；命中转后台派发，不阻塞钩子。</summary>
    private void OnKeyDown(int vk)
    {
        var usage = KeyboardUsageMap.FromVirtualKey(vk);
        if (usage == 0)
            return;

        List<SoftTriggerDefinition>? candidates;
        lock (_gate)
        {
            _byTrigger.TryGetValue(usage, out candidates);
        }
        if (candidates is not { Count: > 0 })
            return;

        // 修饰精确匹配：要求的都按着、没要求的都没按（注入回放期间合成修饰也被看到，动态预留律保证那是"动作键"而非触发键）
        var matches = candidates.Where(d => ModifierMatches(d.TriggerModifier)).ToList();
        if (matches.Count == 0)
            return;

        Log(SoftTriggerEvent.KindTriggerSeen, matches[0].Name,
            $"捕获触发键 {SoftTriggerDescriber.DescribeTrigger(usage, matches[0].TriggerModifier)}（{matches.Count} 条命中）");
        foreach (var def in matches)
            Task.Run(() => Dispatch(def));
    }

    /// <summary>GetAsyncKeyState 实时查询：要求的修饰位全按下、其余修饰位全抬起。</summary>
    private static bool ModifierMatches(byte required)
    {
        foreach (var (bit, vks) in new[] { ((byte)0x01, new[] { 0x11 }), ((byte)0x02, new[] { 0x10 }), ((byte)0x04, new[] { 0x12 }), ((byte)0x08, new[] { 0x5B, 0x5C }) })
        {
            var pressed = vks.Any(vk => (GetAsyncKeyState(vk) & 0x8000) != 0);
            if ((required & bit) != 0 && !pressed)
                return false;
            if ((required & bit) == 0 && pressed)
                return false;
        }
        return true;
    }

    private void Dispatch(SoftTriggerDefinition def)
    {
        try
        {
            if (def.RepeatMode == SoftTriggerRepeatMode.Toggle)
            {
                if (_toggleTimers.TryRemove(def.Name, out var running))
                {
                    running.Dispose();
                    Log(SoftTriggerEvent.KindToggleStopped, def.Name, "连发停止（触发键再按）");
                    return;
                }
                StartToggle(def);
            }
            else
            {
                Replay(def);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "软触发「{Name}」派发失败", def.Name);
            Log(SoftTriggerEvent.KindError, def.Name, $"派发失败：{ex.Message}");
        }
    }

    private void StartToggle(SoftTriggerDefinition def)
    {
        // 立即回放第一遍，再按周期连发（经典连点手感）
        Replay(def);
        var timer = new Timer(_ => Replay(def), null, def.RepeatIntervalMs, def.RepeatIntervalMs);
        if (_toggleTimers.TryAdd(def.Name, timer))
        {
            Log(SoftTriggerEvent.KindToggleStarted, def.Name, $"连发开始（周期 {def.RepeatIntervalMs}ms；触发键再按停止）");
        }
        else
        {
            timer.Dispose();   // 竞态：已被停止/重启——丢弃本次
        }
    }

    private void Replay(SoftTriggerDefinition def)
    {
        try
        {
            _sender.SendSequence(def.Steps);
            Log(SoftTriggerEvent.KindReplayed, def.Name, $"已回放 {def.Steps.Count} 步：{SoftTriggerDescriber.DescribeSequence(def.Steps)}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "软触发「{Name}」回放失败", def.Name);
            Log(SoftTriggerEvent.KindError, def.Name, $"回放失败：{ex.Message}");
        }
    }

    private void StopAllToggles()
    {
        foreach (var kv in _toggleTimers)
        {
            if (_toggleTimers.TryRemove(kv.Key, out var timer))
                timer.Dispose();
        }
    }

    private void OnToggleChanged(object? sender, FeatureToggleChangedEventArgs e)
    {
        if (e.Descriptor.Id == FeatureId && !e.Enabled)
            Stop();
    }

    private void Log(string kind, string name, string detail)
    {
        var evt = new SoftTriggerEvent(DateTime.Now, kind, name, detail);
        lock (_gate)
        {
            _events.Enqueue(evt);
            while (_events.Count > 200)
                _events.Dequeue();
        }
        EventLogged?.Invoke(this, evt);
    }

    public void Dispose()
    {
        _toggles.Changed -= OnToggleChanged;
        Stop();
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
