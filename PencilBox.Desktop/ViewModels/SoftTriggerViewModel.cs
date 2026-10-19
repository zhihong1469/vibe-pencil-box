// <copyright file="SoftTriggerViewModel.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PencilBox.Core.Features;
using PencilBox.Core.Models;
using PencilBox.Core.Services;
using PencilBox.Core.SoftTrigger;
using PencilBox.Desktop.Helpers;
using PencilBox.Desktop.Views;

namespace PencilBox.Desktop.ViewModels;

/// <summary>
/// 软触发页（13 号动作库键盘族，2026-10-19；v2 同日目验反馈重排）：
/// 页面=标题行（右上角「＋ 新建」）+ 定义列表（新编排的从顶部向下生长）+ 底部实时事件；
/// 编辑走 <see cref="SoftTriggerEditDialog"/> 弹窗（步骤编排 ↑↓✕ + 步后延时 + 优先级）——参考 Keymap 的编辑认知。
/// 纯软件域——不依赖任何设备服务；引擎随宿主启动（App.OnStartup 拉起），本页只做定义管理 + 试射 + 事件观察。
/// </summary>
public partial class SoftTriggerViewModel : ObservableObject
{
    private readonly ISoftTriggerStore _store;
    private readonly ISoftTriggerEngine _engine;
    private readonly IFeatureToggleService _toggles;
    private readonly ILogger<SoftTriggerViewModel> _logger;

    public ObservableCollection<SoftTriggerItemViewModel> Definitions { get; } = [];

    /// <summary>引擎实时事件（新在前，截 100 条；钩子/定时器线程触发 → 主线程调度）。</summary>
    public ObservableCollection<SoftTriggerEvent> Events { get; } = [];

    [ObservableProperty]
    private string _lastMessage = "点右上角「＋ 新建软触发」创建第一个；触发键=全键盘池±修饰（F13~F16 工作状态保留），防回环由动态预留律把关。";

    public bool FeatureEnabled => _toggles.IsEnabled("softtrigger");

    /// <summary>引擎运行位（DataTrigger 变色直出 bool——不走转换器，坑点速查 §三）。</summary>
    public bool EngineRunning => FeatureEnabled && _engine.IsRunning;

    public string EngineStatusText =>
        !FeatureEnabled ? "功能已停用（功能控制台或 CLI feature on softtrigger 恢复）"
        : _engine.IsRunning ? "运行中——全键盘池监听已就位，启用定义即生效"
        : "已停机";

    /// <summary>空列表提示显隐（VM 直出 Visibility，同上）。</summary>
    public System.Windows.Visibility EmptyHintVisibility =>
        Definitions.Count == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public SoftTriggerViewModel(
        ISoftTriggerStore store,
        ISoftTriggerEngine engine,
        IFeatureToggleService toggles,
        ILogger<SoftTriggerViewModel> logger)
    {
        _store = store;
        _engine = engine;
        _toggles = toggles;
        _logger = logger;

        _engine.EventLogged += OnEngineEvent;
        _toggles.Changed += OnToggleChanged;
        Refresh();
    }

    // ===== 新建 / 编辑（弹窗编排，v2） =====

    /// <summary>右上角「＋ 新建软触发」：弹编辑对话框 → 保存落盘 → 列表顶部生长。</summary>
    [RelayCommand]
    private void OpenNew()
    {
        if (!FeatureEnabled)
        {
            LastMessage = "软触发功能已停用——先在仪表盘「功能控制台」或 CLI feature on softtrigger 恢复";
            return;
        }
        OpenDialog(editing: null);
    }

    [RelayCommand]
    private void Edit(SoftTriggerItemViewModel? item)
    {
        if (item == null)
            return;
        OpenDialog(item.Definition);
    }

    private void OpenDialog(SoftTriggerDefinition? editing)
    {
        var snapshot = _store.List().ToList();
        var result = SoftTriggerEditDialog.Show(App.MainWindow ?? throw new InvalidOperationException("主窗未就绪"), snapshot, editing);
        if (result == null)
            return;
        try
        {
            _store.Save(result);                               // 单定义 + 动态预留全局律两道（对话框已预检，此处兜并发底）
            _engine.Reload();
            Refresh();
            LastMessage = $"已保存「{result.Name}」：{SoftTriggerDescriber.DescribeTrigger(result.TriggerUsageId, result.TriggerModifier)} → {SoftTriggerDescriber.DescribeSequence(result.Steps)}";
            _logger.LogInformation("软触发已保存：{Name}", result.Name);
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
        {
            LastMessage = $"保存被拒：{ex.Message}";
        }
    }

    // ===== 列表行操作 =====

    [RelayCommand]
    private void Delete(SoftTriggerItemViewModel? item)
    {
        if (item == null)
            return;
        _store.Delete(item.Name);
        _engine.Reload();
        Refresh();
        LastMessage = $"已删除「{item.Name}」";
    }

    /// <summary>启停单条定义（引擎重载即时生效）。</summary>
    [RelayCommand]
    private void SetItemEnabled(SoftTriggerItemViewModel? item)
    {
        if (item == null)
            return;
        var enabling = !item.Definition.Enabled;
        try
        {
            _store.SetEnabled(item.Name, enabling);
            _engine.Reload();
            Refresh();
            LastMessage = $"「{item.Name}」已{(enabling ? "启用" : "停用")}";
        }
        catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException)
        {
            LastMessage = $"失败：{ex.Message}";
        }
    }

    /// <summary>试射：不经钩子直接回放（键击进当前前台窗口）。</summary>
    [RelayCommand]
    private void Fire(SoftTriggerItemViewModel? item)
    {
        if (item == null)
            return;
        try
        {
            _engine.Fire(item.Name);
            LastMessage = $"已试射「{item.Name}」（键击已进前台窗口）";
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            LastMessage = $"试射失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void Refresh()
    {
        var ordered = _store.List()
            .OrderByDescending(d => d.Enabled)
            .ThenByDescending(d => d.CreatedAt)                // v2：新编排的从顶部向下生长
            .ToList();
        Definitions.Clear();
        foreach (var def in ordered)
            Definitions.Add(new SoftTriggerItemViewModel(def));

        OnPropertyChanged(nameof(FeatureEnabled));
        OnPropertyChanged(nameof(EngineRunning));
        OnPropertyChanged(nameof(EngineStatusText));
        OnPropertyChanged(nameof(EmptyHintVisibility));
    }

    // ===== 事件 =====

    private void OnEngineEvent(object? sender, SoftTriggerEvent e)
    {
        MainThreadHelper.EnsureOnMainThread(() =>
        {
            Events.Insert(0, e);
            while (Events.Count > 100)
                Events.RemoveAt(Events.Count - 1);
            OnPropertyChanged(nameof(EngineRunning));
            OnPropertyChanged(nameof(EngineStatusText));
        });
    }

    private void OnToggleChanged(object? sender, FeatureToggleChangedEventArgs e)
    {
        if (e.Descriptor.Id != "softtrigger")
            return;

        MainThreadHelper.EnsureOnMainThread(() =>
        {
            // 引擎停机由引擎自己的 Changed 订阅负责；这里补"重新启用→拉起"+状态文案
            if (e.Enabled && !_engine.IsRunning)
            {
                try
                {
                    _engine.Start();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "软触发引擎拉起失败");
                }
            }
            Refresh();
        });
    }
}

/// <summary>软触发列表一行。</summary>
public class SoftTriggerItemViewModel
{
    public SoftTriggerDefinition Definition { get; }

    public string Name { get; }
    public string TriggerText { get; }
    public string SequenceText { get; }
    public string ModeText { get; }
    public string Note { get; }
    public int Priority { get; }

    public SoftTriggerItemViewModel(SoftTriggerDefinition definition)
    {
        Definition = definition;
        Name = definition.Name;
        TriggerText = SoftTriggerDescriber.DescribeTrigger(definition.TriggerUsageId, definition.TriggerModifier);
        SequenceText = SoftTriggerDescriber.DescribeSequence(definition.Steps);
        ModeText = definition.RepeatMode == SoftTriggerRepeatMode.Toggle
            ? $"连发 / {definition.RepeatIntervalMs}ms"
            : "单次";
        Note = definition.Note;
        Priority = definition.Priority;
    }

    /// <summary>启用位（复选框两路绑定 → VM 命令持久化）。</summary>
    public bool IsEnabled => Definition.Enabled;
}
