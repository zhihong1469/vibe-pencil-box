// <copyright file="SoftTriggerEngine.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PencilBox.Core.Features;
using PencilBox.Core.Models;
using PencilBox.Core.Services;
using PencilBox.Core.SoftTrigger;

namespace PencilBox.Infrastructure.Services;

/// <summary>
/// <see cref="ISoftTriggerEngine"/> 实现（动作库键盘族 13 号，2026-10-19）。
/// 观测腿=共享全局键盘钩子（工作状态监听共用一条 WH_KEYBOARD_LL，触发域 F17~F24 不相交）；
/// 发腿=<see cref="ISyntheticKeyboard"/>（SendInput）。值域分离防回环：动作禁入专用键段，
/// 钩子即使收到本引擎回放的键也不会匹配任何触发键。
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
    private Dictionary<byte, List<SoftTriggerDefinition>> _byTrigger = new();   // 触发键 usage → 启用定义

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
        Log(SoftTriggerEvent.KindTriggerSeen, "引擎", "引擎已启动（钩子观测腿就位，触发域 F17~F24）");
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

    /// <summary>装载启用定义（值域校验过的才可能在库里；防御性再验，坏定义拒载并记事件）。调用方持 _gate。</summary>
    private void ReloadCore()
    {
        var byTrigger = new Dictionary<byte, List<SoftTriggerDefinition>>();
        foreach (var def in _store.List())
        {
            if (!def.Enabled)
                continue;
            try
            {
                SoftTriggerKeys.Validate(def);
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "软触发「{Name}」校验失败，拒载", def.Name);
                Log(SoftTriggerEvent.KindError, def.Name, $"装载被拒：{ex.Message}");
                continue;
            }
            if (!byTrigger.TryGetValue(def.TriggerUsageId, out var list))
                byTrigger[def.TriggerUsageId] = list = [];
            list.Add(def);
        }
        _byTrigger = byTrigger;
    }

    /// <summary>钩子线程回调：只认触发键域（其余键零开销短路）；命中即转后台派发，不阻塞钩子。</summary>
    private void OnKeyDown(int vk)
    {
        var usage = KeyboardUsageMap.FromVirtualKey(vk);
        if (!SoftTriggerKeys.IsTriggerDomain(usage))
            return;

        List<SoftTriggerDefinition>? matches;
        lock (_gate)
        {
            _byTrigger.TryGetValue(usage, out matches);
        }
        if (matches is not { Count: > 0 })
            return;

        Log(SoftTriggerEvent.KindTriggerSeen, matches[0].Name, $"捕获触发键 {SoftTriggerKeys.TriggerName(usage)}（{matches.Count} 条定义命中）");
        foreach (var def in matches.ToArray())
            Task.Run(() => Dispatch(def));
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
            _sender.SendSequence(def.Steps, def.StepIntervalMs);
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
}
