// <copyright file="SoftTriggerCommands.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Cli.Output;
using PencilBox.Core.Features;
using PencilBox.Core.Models;
using PencilBox.Core.Services;
using PencilBox.Core.SoftTrigger;

namespace PencilBox.Cli.Commands;

/// <summary>
/// `softtrigger` 域（动作库键盘族 13 号，2026-10-19）：专用键 F17~F24 触发键盘序列回放。
/// 纯软件域——零设备依赖；回环自证 `selftest` = 注入触发键 → 钩子捕获 → 回放 → 事件流断言，全程零设备。
/// 引擎寄宿于 Desktop 进程；CLI 一次性进程只提供定义 CRUD / 手动试射 / selftest（进程内临时拉引擎）。
/// </summary>
public class SoftTriggerCommands
{
    private readonly ISoftTriggerStore _store;
    private readonly ISoftTriggerEngine _engine;
    private readonly ISyntheticKeyboard _sender;
    private readonly IFeatureToggleService _toggles;

    public SoftTriggerCommands(
        ISoftTriggerStore store,
        ISoftTriggerEngine engine,
        ISyntheticKeyboard sender,
        IFeatureToggleService toggles)
    {
        _store = store;
        _engine = engine;
        _sender = sender;
        _toggles = toggles;
    }

    public int List()
    {
        var defs = _store.List();
        if (defs.Count == 0)
        {
            ConsoleFormatter.Info("暂无软触发定义。示例：softtrigger add 语音识别 --trigger F17 --keys \"F2,ALT+L\"");
            return 0;
        }

        ConsoleFormatter.PrintTable(
            defs,
            ("名称", d => d.Name),
            ("触发键", d => SoftTriggerKeys.TriggerName(d.TriggerUsageId)),
            ("模式", d => d.RepeatMode == SoftTriggerRepeatMode.Toggle ? $"连发/{d.RepeatIntervalMs}ms" : "单次"),
            ("序列", d => SoftTriggerDescriber.DescribeSequence(d.Steps)),
            ("步隔", d => $"{d.StepIntervalMs}ms"),
            ("启用", d => d.Enabled ? "开" : "关"),
            ("备注", d => d.Note));
        ConsoleFormatter.Info("引擎寄宿于 Desktop 进程（启动即生效）；CLI 侧验证用 run（手动试射）或 selftest（全链回环）。");
        return 0;
    }

    public int Add(
        string name, string trigger, string keys,
        string? mode, int stepInterval, int repeatInterval, string? note, bool disabled)
    {
        try
        {
            var def = new SoftTriggerDefinition
            {
                Name = name,
                Enabled = !disabled,
                TriggerUsageId = SoftTriggerKeys.ParseTriggerName(trigger),
                Steps = SoftTriggerTextParser.ParseSequence(keys),
                StepIntervalMs = stepInterval,
                RepeatMode = ParseMode(mode),
                RepeatIntervalMs = repeatInterval,
                Note = note ?? "",
            };
            SoftTriggerKeys.Validate(def);   // 存储还会再拦一道，这里提前给可读报错
            _store.Save(def);
            ConsoleFormatter.Success(
                $"已保存「{name}」：{SoftTriggerKeys.TriggerName(def.TriggerUsageId)} → {SoftTriggerDescriber.DescribeSequence(def.Steps)}" +
                (def.RepeatMode == SoftTriggerRepeatMode.Toggle ? $"（连发 {def.RepeatIntervalMs}ms）" : "（单次）") +
                (def.Enabled ? "" : "（当前停用，softtrigger on 开启）"));
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
        {
            ConsoleFormatter.Error(ex.Message);
            return 1;
        }
    }

    public int Remove(string name)
    {
        if (_store.Load(name) == null)
        {
            ConsoleFormatter.Error($"软触发不存在：{name}");
            return 1;
        }
        _store.Delete(name);
        ConsoleFormatter.Success($"已删除「{name}」");
        return 0;
    }

    public int SetEnabled(string name, bool enabled)
    {
        try
        {
            _store.SetEnabled(name, enabled);
            ConsoleFormatter.Success(enabled
                ? $"已启用「{name}」（Desktop 引擎下次装载/重载生效）"
                : $"已停用「{name}」");
            return 0;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException)
        {
            ConsoleFormatter.Error(ex.Message);
            return 1;
        }
    }

    /// <summary>手动试射：不经钩子直接回放（无 F17 物理键时的验证手段；键击进当前前台窗口）。</summary>
    public async Task<int> RunAsync(string name, int count)
    {
        var def = _store.Load(name);
        if (def == null)
        {
            ConsoleFormatter.Error($"软触发不存在：{name}");
            return 1;
        }

        if (!def.Enabled)
        {
            ConsoleFormatter.Error($"「{name}」当前停用——先 softtrigger on {name}");
            return 1;
        }

        count = Math.Clamp(count, 1, 100);
        ConsoleFormatter.Info($"试射「{name}」×{count}：{SoftTriggerDescriber.DescribeSequence(def.Steps)}（键击将进前台窗口，请切好焦点）");
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
                await Task.Delay(Math.Max(50, def.RepeatIntervalMs));
            try
            {
                _sender.SendSequence(def.Steps, def.StepIntervalMs);
                ConsoleFormatter.Success($"[{i + 1}/{count}] 已回放");
            }
            catch (Exception ex)
            {
                ConsoleFormatter.Error($"回放失败：{ex.Message}");
                return 1;
            }
        }
        return 0;
    }

    /// <summary>裸发送键序列（不带定义直接试手感；同样走值域守卫——专用键段拒发）。</summary>
    public int Send(string keys, int interval)
    {
        try
        {
            var steps = SoftTriggerTextParser.ParseSequence(keys);
            foreach (var step in steps.Where(s => SoftTriggerKeys.IsReservedRange(s.Usage)))
                throw new ArgumentException($"键值 0x{step.Usage:X2}（F{step.Usage - SoftTriggerKeys.ReservedLo + 13}）属专用键保留段，拒绝发送（防回环/防误扰工作状态）");
            ConsoleFormatter.Info($"裸发送：{SoftTriggerDescriber.DescribeSequence(steps)}（步隔 {interval}ms；键击将进前台窗口）");
            _sender.SendSequence(steps, interval);
            ConsoleFormatter.Success("已发送");
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            ConsoleFormatter.Error(ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// **回环自证**（13 号 CLI-first 验收路径，零设备零人工）：进程内拉起引擎 → 注入触发键（SendInput F24）→
    /// 钩子捕获 → 引擎回放 F7 → 事件流断言 Replayed。全链闭环即 PASS（含超时归因提示）。
    /// 注意：会向系统注入真实键击 F24/F7（无副作用键；前台窗口可能收到，勿在输入框焦点处执行）。
    /// </summary>
    public async Task<int> Selftest()
    {
        if (!_toggles.IsEnabled("softtrigger"))
        {
            ConsoleFormatter.Error("软触发总开关已停用——先执行 pencilbox feature on softtrigger 再自测");
            return 1;
        }

        const string defName = "_selftest_回环";
        var tcs = new TaskCompletionSource<SoftTriggerEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seenTcs = new TaskCompletionSource<SoftTriggerEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        ConsoleFormatter.Info("软触发回环自证开始：拉起引擎 → 注入 F24 → 期望钩子捕获并回放 F7（超时 5s）");
        var def = new SoftTriggerDefinition
        {
            Name = defName,
            Enabled = true,
            TriggerUsageId = SoftTriggerKeys.ParseTriggerName("F24"),
            Steps = [SoftTriggerTextParser.ParseStep("F7")],
            StepIntervalMs = 0,
            Note = "selftest 临时定义",
        };

        try
        {
            _store.Save(def);
            _engine.EventLogged += OnEvent;
            _engine.Start();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            // 注入触发键：同一条 SendInput 发腿 → OS → 钩子 → 引擎，真实闭环
            _sender.SendSequence([new SoftTriggerStep(SoftTriggerKeys.ParseTriggerName("F24"), 0)], 0);

            var seenTask = await Task.WhenAny(seenTcs.Task, Task.Delay(5000));
            if (seenTask != seenTcs.Task)
                return Fail("钩子未捕获注入的 F24", "① 非交互会话收不到低级钩子；② 火绒等拦截了 SendInput（本机有拦麦前科）；③ 键被其他钩子吞了");
            var seenMs = sw.ElapsedMilliseconds;

            var replayTask = await Task.WhenAny(tcs.Task, Task.Delay(5000));
            if (replayTask != tcs.Task)
                return Fail($"钩子已捕获（{seenMs}ms）但引擎未回放", "① 定义未装载（Reload 未触发）；② 值域校验拒载（看事件流）；③ 发腿 SendInput 失败");

            ConsoleFormatter.Success($"PASS——回环闭环：注入 F24 → 钩子捕获（{seenMs}ms）→ 引擎回放 F7（{sw.ElapsedMilliseconds}ms 总时长）");
            foreach (var e in _engine.RecentEvents(10))
                Console.WriteLine($"    [{e.Time:HH:mm:ss.fff}] {e.Kind} {e.Name}: {e.Detail}");
            return 0;
        }
        catch (Exception ex)
        {
            ConsoleFormatter.Error($"自测异常：{ex.Message}");
            return 1;
        }
        finally
        {
            _engine.EventLogged -= OnEvent;
            _engine.Stop();
            _store.Delete(defName);
        }

        int Fail(string what, string hints)
        {
            ConsoleFormatter.Error($"FAIL——{what}");
            ConsoleFormatter.Warn($"归因提示：{hints}");
            foreach (var e in _engine.RecentEvents(10))
                Console.WriteLine($"    [{e.Time:HH:mm:ss.fff}] {e.Kind} {e.Name}: {e.Detail}");
            return 1;
        }

        void OnEvent(object? sender, SoftTriggerEvent e)
        {
            if (e.Name != defName)
                return;
            if (e.Kind == SoftTriggerEvent.KindTriggerSeen)
                seenTcs.TrySetResult(e);
            if (e.Kind == SoftTriggerEvent.KindReplayed)
                tcs.TrySetResult(e);
        }
    }

    private static SoftTriggerRepeatMode ParseMode(string? mode) => (mode ?? "once").Trim().ToLowerInvariant() switch
    {
        "once" or "单次" or "1" => SoftTriggerRepeatMode.Once,
        "toggle" or "连发" or "2" => SoftTriggerRepeatMode.Toggle,
        _ => throw new ArgumentException($"未知模式：{mode}（once=单次 / toggle=开关连发）"),
    };
}
