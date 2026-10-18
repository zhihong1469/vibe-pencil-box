// <copyright file="WorkStateCommands.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using Microsoft.Extensions.Logging;
using PencilBox.Cli.Output;
using PencilBox.Core.Models;
using PencilBox.Core.Protocol;
using PencilBox.Core.Services;

namespace PencilBox.Cli.Commands;

/// <summary>
/// `workstate` 域——笔端工作状态自管（产品落地/byte2…/PC 落地回执 §三 立案落地，2026-10-18）。
///
/// byte2 工作模式退役后，工作状态权威=事件流：识别启停=K2 会话边界（0x13 → 基站映射热键），
/// 扩音档=S1 切换表（0x16 → 热键）。上位机对基站上报 OS 的热键做全局监听（被动不吞键），
/// 自维护"识别 × 扩音档"两轴。触发键绑定**从 Keymap 配置推导**——换绑定=改 Keymap 表，无固件参与。
/// </summary>
public sealed class WorkStateCommands
{
    private readonly IKeymapConfigStore _store;
    private readonly IGlobalKeyboardHook _hook;
    private readonly ILogger<WorkStateCommands> _logger;

    public WorkStateCommands(IKeymapConfigStore store, IGlobalKeyboardHook hook, ILogger<WorkStateCommands> logger)
    {
        _store = store;
        _hook = hook;
        _logger = logger;
    }

    /// <summary>`workstate binding [--config 名]`：展示从 Keymap 配置推导的四键绑定与可用性</summary>
    public int Binding(string? name)
    {
        var (binding, error) = ResolveBinding(name);
        if (error != null)
        {
            ConsoleFormatter.Error(error);
            return 1;
        }
        PrintBinding(binding);
        return 0;
    }

    /// <summary>
    /// `workstate listen [--config 名] [--secs N]`：安装全局键盘钩子，实时打印状态转移。
    /// `--secs` 缺省=一直监听直到 Ctrl+C。被动监听（CallNextHookEx 恒转发），外部热键软件不受影响。
    /// </summary>
    public int Listen(string? name, int secs)
    {
        var (binding, error) = ResolveBinding(name);
        if (error != null)
        {
            ConsoleFormatter.Error(error);
            return 1;
        }
        if (!binding.IsUsable)
        {
            ConsoleFormatter.Error($"绑定不完整（识别开始/结束、扩音开/关四条腿须齐备）：{DescribeBinding(binding)}");
            ConsoleFormatter.Error("提示：联动键须为键盘动作（F 区/字母/数字等），消费键/鼠标动作与带修饰键的组合观测不到");
            return 1;
        }
        PrintBinding(binding);
        ConsoleFormatter.Info(secs > 0
            ? $"监听中（{secs}s 后自动停止）——按绑定键观察状态转移"
            : "监听中——按绑定键观察状态转移，Ctrl+C 停止");

        var tracker = new PenWorkStateTracker(binding);
        _hook.KeyDown += vk =>
        {
            if (tracker.Feed(KeyboardUsageMap.FromVirtualKey(vk)))
                ConsoleFormatter.Info($"[{DateTime.Now:HH:mm:ss}] 状态 → {tracker.DisplayText}");
        };
        _hook.Start();
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        try
        {
            cts.Token.WaitHandle.WaitOne(secs > 0 ? TimeSpan.FromSeconds(secs) : Timeout.InfiniteTimeSpan);
        }
        finally
        {
            _hook.Stop();
        }
        ConsoleFormatter.Info($"监听结束。最终状态：{tracker.DisplayText}");
        return 0;
    }

    // ===== 内部 =====

    private (PenWorkStateBinding Binding, string? Error) ResolveBinding(string? name)
    {
        _store.Initialize();
        name = string.IsNullOrWhiteSpace(name) ? _store.GetCurrentName() : name;
        var doc = _store.Load(name);
        if (doc == null)
            return (null!, $"配置不存在或不可读：{name}");
        // 切换表未随配置携带（旧文件）→ 回落出厂真值（与 GUI/`keymap show` 同口径）
        var switchEntries = doc.SwitchEntries ?? KeymapFactoryDefault.SwitchEntries.ToList();
        return (PenWorkStateBinding.FromKeymap(doc.Entries, switchEntries, name), null);
    }

    private static void PrintBinding(PenWorkStateBinding b)
    {
        ConsoleFormatter.Info($"配置「{b.SourceName}」→ 笔端工作状态触发键（绑定{(b.IsUsable ? "完整" : "不完整——缺失腿被忽略")}）");
        ConsoleFormatter.Info($"  识别会话开始（K2 长按→0x03）  : {UsageName(b.SessionStartUsage)}");
        ConsoleFormatter.Info($"  识别会话结束（K2 长放→0x04）  : {UsageName(b.SessionEndUsage)}");
        ConsoleFormatter.Info($"  扩音开（S1=SPK 档→0x16）      : {UsageName(b.AmpOnUsage)}");
        ConsoleFormatter.Info($"  扩音关（S1=ON 档→0x16）       : {UsageName(b.AmpOffUsage)}");
    }

    private static string DescribeBinding(PenWorkStateBinding b) =>
        $"开始={UsageName(b.SessionStartUsage)} 结束={UsageName(b.SessionEndUsage)} " +
        $"扩音开={UsageName(b.AmpOnUsage)} 扩音关={UsageName(b.AmpOffUsage)}";

    private static string UsageName(byte usage) =>
        usage == 0 ? "（未绑定）" : KeymapFormatter.GetCodeName(KeyActionType.Keyboard, usage, 0);
}
