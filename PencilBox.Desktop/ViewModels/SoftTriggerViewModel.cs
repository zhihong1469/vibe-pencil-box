// <copyright file="SoftTriggerViewModel.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PencilBox.Core.Features;
using PencilBox.Core.Models;
using PencilBox.Core.Services;
using PencilBox.Core.SoftTrigger;
using PencilBox.Desktop.Helpers;

namespace PencilBox.Desktop.ViewModels;

/// <summary>
/// 软触发页（13 号动作库键盘族，2026-10-19）：F17~F24 专用键触发键盘序列回放。
/// 纯软件域——不依赖任何设备服务；引擎随宿主启动（App.OnStartup 拉起），本页只做定义管理 + 试射 + 事件观察。
/// 值域分离防回环由存储/装载两道 <see cref="SoftTriggerKeys.Validate"/> 把关，编辑面坏输入落盘前即被拒。
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
    private string _editorName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorPreview))]
    private string _editorTrigger = "F17";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorPreview))]
    private string _editorKeys = "";

    [ObservableProperty]
    private int _editorModeIndex;                          // 0=单次 1=连发（ComboBox SelectedIndex 直绑）

    [ObservableProperty]
    private int _editorStepInterval = 60;

    [ObservableProperty]
    private int _editorRepeatInterval = 200;

    [ObservableProperty]
    private string _editorNote = "";

    [ObservableProperty]
    private string _lastMessage = "触发键=专用键 F17~F24；动作序列为普通键（禁入 F13~F24 保留段，防回环）。";

    public bool FeatureEnabled => _toggles.IsEnabled("softtrigger");

    /// <summary>引擎运行位（DataTrigger 变色直出 bool——不走转换器，坑点速查 §三）。</summary>
    public bool EngineRunning => FeatureEnabled && _engine.IsRunning;

    public string EngineStatusText =>
        !FeatureEnabled ? "功能已停用（功能控制台或 CLI feature on softtrigger 恢复）"
        : _engine.IsRunning ? "运行中——F17~F24 监听已就位，启用定义即生效"
        : "已停机";

    /// <summary>空列表提示显隐（VM 直出 Visibility，同上）。</summary>
    public System.Windows.Visibility EmptyHintVisibility =>
        Definitions.Count == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public IReadOnlyList<string> TriggerKeyChoices { get; } = ["F17", "F18", "F19", "F20", "F21", "F22", "F23", "F24"];

    /// <summary>编辑器当前输入 → 序列预览（解析失败显示原因，不打断输入）。</summary>
    public string EditorPreview
    {
        get
        {
            try
            {
                return SoftTriggerDescriber.DescribeSequence(SoftTriggerTextParser.ParseSequence(EditorKeys));
            }
            catch (ArgumentException ex)
            {
                return $"（待有效输入：{ex.Message}）";
            }
        }
    }

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

    // ===== 命令 =====

    [RelayCommand]
    private void Save()
    {
        try
        {
            var def = new SoftTriggerDefinition
            {
                Name = EditorName.Trim(),
                Enabled = true,
                TriggerUsageId = SoftTriggerKeys.ParseTriggerName(EditorTrigger),
                Steps = SoftTriggerTextParser.ParseSequence(EditorKeys),
                StepIntervalMs = EditorStepInterval,
                RepeatMode = EditorModeIndex == 1 ? SoftTriggerRepeatMode.Toggle : SoftTriggerRepeatMode.Once,
                RepeatIntervalMs = EditorRepeatInterval,
                Note = EditorNote.Trim(),
            };
            _store.Save(def);                                  // 值域不合规在此抛
            _engine.Reload();
            Refresh();
            LastMessage = $"已保存「{def.Name}」：{SoftTriggerKeys.TriggerName(def.TriggerUsageId)} → {SoftTriggerDescriber.DescribeSequence(def.Steps)}";
            _logger.LogInformation("软触发已保存：{Name}", def.Name);
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
        {
            LastMessage = $"保存被拒：{ex.Message}";
        }
    }

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

    /// <summary>启停单条定义（引擎重载即时生效；连发中的按名终止与否由引擎 Reload 语义决定）。</summary>
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
        Definitions.Clear();
        foreach (var def in _store.List())
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
public partial class SoftTriggerItemViewModel : ObservableObject
{
    public SoftTriggerDefinition Definition { get; }

    public string Name { get; }
    public string TriggerName { get; }
    public string SequenceText { get; }
    public string ModeText { get; }
    public string Note { get; }

    public SoftTriggerItemViewModel(SoftTriggerDefinition definition)
    {
        Definition = definition;
        Name = definition.Name;
        TriggerName = SoftTriggerKeys.TriggerName(definition.TriggerUsageId);
        SequenceText = SoftTriggerDescriber.DescribeSequence(definition.Steps);
        ModeText = definition.RepeatMode == SoftTriggerRepeatMode.Toggle
            ? $"连发 / {definition.RepeatIntervalMs}ms"
            : "单次";
        Note = definition.Note;
    }

    /// <summary>启用位（复选框两路绑定 → VM 命令持久化）。</summary>
    public bool IsEnabled => Definition.Enabled;
}
