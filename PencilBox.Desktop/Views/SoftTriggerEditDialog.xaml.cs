// <copyright file="SoftTriggerEditDialog.xaml.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using PencilBox.Core.Models;
using PencilBox.Core.SoftTrigger;

namespace PencilBox.Desktop.Views;

/// <summary>
/// 软触发编辑对话框（13 号 v2，2026-10-19 指挥官目验反馈）：
/// 参考 Keymap 的编辑认知——新建弹窗编排、步骤行 ↑↓✕ 排序删增、每步带步后延时；
/// 保存前单定义值域预检 + 动态预留全局律预检（传入当前全表快照），错误就地红字不打断输入。
/// </summary>
public partial class SoftTriggerEditDialog : Window
{
    /// <summary>
    /// 修饰组合选项（位和 0x0F 全 16 态；触发与动作步共用）。
    /// 必须用带属性的 KeyValue——ValueTuple 的字段名是编译期糖，运行时反射取不到，
    /// SelectedValuePath/DisplayMemberPath 会静默失效 → 下拉空白（2026-10-19 目验实锤，v2.1 修）。
    /// </summary>
    public static readonly KeyValue[] ModifierOptions =
    [
        new(0x00, "无"),
        new(0x01, "Ctrl"),
        new(0x02, "Shift"),
        new(0x04, "Alt"),
        new(0x08, "Win"),
        new(0x03, "Ctrl+Shift"),
        new(0x05, "Ctrl+Alt"),
        new(0x09, "Ctrl+Win"),
        new(0x06, "Shift+Alt"),
        new(0x0A, "Shift+Win"),
        new(0x0C, "Alt+Win"),
        new(0x07, "Ctrl+Shift+Alt"),
        new(0x0B, "Ctrl+Shift+Win"),
        new(0x0D, "Ctrl+Alt+Win"),
        new(0x0E, "Shift+Alt+Win"),
        new(0x0F, "Ctrl+Shift+Alt+Win"),
    ];

    public ObservableCollection<StepRow> Steps { get; } = [];

    /// <summary>保存结果（DialogResult=true 时有效）。</summary>
    public SoftTriggerDefinition Result { get; private set; } = new();

    private readonly List<SoftTriggerDefinition> _existingDefs;
    private readonly SoftTriggerDefinition? _editing;

    private SoftTriggerEditDialog(List<SoftTriggerDefinition> existingDefs, SoftTriggerDefinition? editing)
    {
        InitializeComponent();
        _existingDefs = existingDefs;
        _editing = editing;

        TriggerModifierBox.ItemsSource = ModifierOptions;
        TriggerModifierBox.SelectedValue = (byte)0;
        TriggerKeyBox.ItemsSource = KeyOptionsForTrigger().ToList();
        TriggerKeyBox.SelectedValue = (byte)0x73;              // 默认 F24（物理键盘不存在，最安全的触发键）
        ModeBox.SelectedIndex = 0;
        StepsList.ItemsSource = Steps;

        Title = editing == null ? "新建连点器" : $"编辑连点器——{editing.Name}";
        if (editing != null)
        {
            NameBox.Text = editing.Name;
            TriggerModifierBox.SelectedValue = editing.TriggerModifier;
            TriggerKeyBox.SelectedValue = editing.TriggerUsageId;
            ModeBox.SelectedIndex = editing.RepeatMode == SoftTriggerRepeatMode.Toggle ? 1 : 0;
            RepeatIntervalBox.Text = editing.RepeatIntervalMs.ToString();
            PriorityBox.Text = editing.Priority.ToString();
            NoteBox.Text = editing.Note;
            foreach (var step in editing.Steps)
                Steps.Add(new StepRow(step));
        }
        if (Steps.Count == 0)
            Steps.Add(new StepRow(new SoftTriggerStep(0x3B, 0)));   // 预置一行 F2，降低空白门槛
    }

    /// <summary>模态弹出；保存返回编排结果，取消返回 null。</summary>
    public static SoftTriggerDefinition? Show(Window owner, List<SoftTriggerDefinition> existingDefs, SoftTriggerDefinition? editing = null)
    {
        var dialog = new SoftTriggerEditDialog(existingDefs, editing) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    /// <summary>触发键下拉项：全键盘池（排除 F13~F16 工作状态保留段）。</summary>
    private static IEnumerable<KeyValue> KeyOptionsForTrigger() =>
        AllKeyOptions().Where(k => !SoftTriggerKeys.IsWorkStateReserved(k.Value));

    /// <summary>动作键下拉项：全键盘池（含 F17~F24——动态预留律在保存时把关）。</summary>
    private static IEnumerable<KeyValue> AllKeyOptions()
    {
        for (var u = 0x04; u <= 0x1D; u++) yield return new KeyValue((byte)u, SoftTriggerDescriber.MainKeyName((byte)u));   // A~Z
        for (var u = 0x1E; u <= 0x27; u++) yield return new KeyValue((byte)u, SoftTriggerDescriber.MainKeyName((byte)u));   // 1~9/0
        for (var u = 0x3A; u <= 0x45; u++) yield return new KeyValue((byte)u, SoftTriggerDescriber.MainKeyName((byte)u));   // F1~F12
        for (var u = 0x68; u <= 0x73; u++) yield return new KeyValue((byte)u, SoftTriggerDescriber.MainKeyName((byte)u));   // F13~F24
        foreach (var u in new byte[] { 0x28, 0x29, 0x2A, 0x2B, 0x2C, 0x49, 0x4A, 0x4B, 0x4C, 0x4D, 0x4E, 0x4F, 0x50, 0x51, 0x52 })
            yield return new KeyValue(u, SoftTriggerDescriber.MainKeyName(u));                                              // 导航/方向
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RepeatIntervalBox != null)
            RepeatIntervalBox.IsEnabled = ModeBox?.SelectedIndex == 1;
    }

    private void OnStepAdd(object sender, RoutedEventArgs e) => Steps.Add(new StepRow(new SoftTriggerStep(0x04, 0)));

    private void OnStepDelete(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is StepRow row)
            Steps.Remove(row);
    }

    private void OnStepUp(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not StepRow row)
            return;
        var i = Steps.IndexOf(row);
        if (i > 0)
            Steps.Move(i, i - 1);
    }

    private void OnStepDown(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not StepRow row)
            return;
        var i = Steps.IndexOf(row);
        if (i >= 0 && i < Steps.Count - 1)
            Steps.Move(i, i + 1);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        try
        {
            var steps = Steps.Select(r => new SoftTriggerStep(r.Usage, r.Modifier, r.DelayMsBefore)).ToList();
            var def = new SoftTriggerDefinition
            {
                Name = NameBox.Text.Trim(),
                Enabled = _editing?.Enabled ?? true,
                TriggerUsageId = TriggerKeyBox.SelectedValue is byte tu ? tu : (byte)0,
                TriggerModifier = TriggerModifierBox.SelectedValue is byte tm ? tm : (byte)0,
                Steps = steps,
                Priority = int.TryParse(PriorityBox.Text, out var p) ? p : 0,
                RepeatMode = ModeBox.SelectedIndex == 1 ? SoftTriggerRepeatMode.Toggle : SoftTriggerRepeatMode.Once,
                RepeatIntervalMs = int.TryParse(RepeatIntervalBox.Text, out var r) ? r : 200,
                Note = NoteBox.Text.Trim(),
                CreatedAt = _editing?.CreatedAt ?? DateTime.Now,
            };
            SoftTriggerKeys.Validate(def);

            // 动态预留全局律预检：模拟"保存后启用集"（与存储层同口径，错误就地显示）
            var enabledAfterSave = _existingDefs
                .Where(d => d.Enabled && !string.Equals(d.Name, def.Name, StringComparison.Ordinal))
                .ToList();
            if (def.Enabled)
                enabledAfterSave.Add(def);
            SoftTriggerKeys.ValidateGlobal(enabledAfterSave);

            Result = def;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            ErrorText.Text = ex.Message;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>下拉项（值+标签；SelectedValuePath 绑定）。</summary>
    public sealed record KeyValue(byte Value, string Label);

    /// <summary>步骤行编辑态。</summary>
    public sealed class StepRow : System.ComponentModel.INotifyPropertyChanged
    {
        private byte _usage;
        private byte _modifier;
        private int _delayMsBefore;

        // 绑定面用实例属性（Binding 对静态属性支持不稳，坑点速查同族）
        public KeyValue[] ModifierOptions { get; } = SoftTriggerEditDialog.ModifierOptions;

        public List<KeyValue> KeyOptions { get; } = AllKeyOptions().ToList();

        public StepRow(SoftTriggerStep step)
        {
            _usage = step.Usage;
            _modifier = step.Modifier;
            _delayMsBefore = step.DelayMsBefore;
        }

        public byte Usage { get => _usage; set { _usage = value; Changed(nameof(Usage)); } }
        public byte Modifier { get => _modifier; set { _modifier = value; Changed(nameof(Modifier)); } }
        public int DelayMsBefore { get => _delayMsBefore; set { _delayMsBefore = value; Changed(nameof(DelayMsBefore)); } }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
    }
}
