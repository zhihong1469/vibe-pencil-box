// <copyright file="KeymapRowViewModel.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using CommunityToolkit.Mvvm.ComponentModel;
using PencilBox.Core.Models;
using PencilBox.Core.Protocol;

namespace PencilBox.Desktop.ViewModels;

/// <summary>
/// Keymap 编辑行（UI 模型，包装 KeymapEntry 供一键一卡视图展示/编辑）
/// 与 Core 的 KeymapEntry（协议模型）解耦，编辑后通过 ToEntry() 回写。
/// 键值不再暴露原始字节编辑框：键盘动作用捕获框（KeyCaptureBox）录入，
/// 消费键/鼠标键用下拉预设，F13~F16 等物理键盘不存在的键走"专用键"下拉。
/// </summary>
public partial class KeymapRowViewModel : ObservableObject
{
    /// <summary>在接收器表中的索引（0-based；行增删后由 VM 重排）</summary>
    public byte Index { get; set; }

    /// <summary>本行是否可编辑（出厂默认配置整体只读）</summary>
    public bool IsEditable { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyName))]
    [NotifyPropertyChangedFor(nameof(EventTypeName))]
    [NotifyPropertyChangedFor(nameof(CanEditEvent))]
    [NotifyPropertyChangedFor(nameof(EventLockTip))]
    [NotifyPropertyChangedFor(nameof(IsK2Row))]
    [NotifyPropertyChangedFor(nameof(IsDeletable))]
    private byte _keyId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EventTypeName))]
    private KeyEventType _eventType;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionTypeName))]
    [NotifyPropertyChangedFor(nameof(CodeName))]
    private KeyActionType _actionType;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModifierName))]
    private byte _modifier;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CodeName))]
    private byte _codeLo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CodeName))]
    private byte _codeHi;

    [ObservableProperty]
    private byte _codeEx;

    public KeymapRowViewModel(byte index, KeymapEntry entry, bool isEditable = true)
    {
        Index = index;
        IsEditable = isEditable;
        _keyId = entry.KeyId;
        _eventType = entry.EventType;
        _actionType = entry.ActionType;
        _modifier = entry.Modifier;
        _codeLo = entry.CodeLo;
        _codeHi = entry.CodeHi;
        _codeEx = entry.CodeEx;
    }

    // 友好显示名（只读）
    public string KeyName => KeymapFormatter.GetKeyName(KeyId);
    public string EventTypeName => KeymapFormatter.GetEventDisplay(KeyId, EventType);
    public string ActionTypeName => KeymapFormatter.GetActionTypeName(ActionType);
    public string ModifierName => KeymapFormatter.GetModifierName(Modifier);
    public string CodeName => KeymapFormatter.GetCodeName(ActionType, CodeLo, CodeHi);

    /// <summary>是否 K2 行（语音会话边界契约，pc-issues/04 §2A）</summary>
    public bool IsK2Row => KeymapRules.IsK2(KeyId);

    /// <summary>
    /// 是否 K2 边界契约行（长按=会话开始/长松=会话结束，不可删）。
    /// K2 上误加的其他事件行（如 0x00 垃圾行）不属于契约，可删——加得进就要删得掉（2026-10-18）。
    /// </summary>
    public bool IsK2BoundaryRow => IsK2Row && KeymapRules.IsK2EventAllowed(EventType);

    /// <summary>是否显示删除按钮（可编辑配置的非契约行）</summary>
    public bool IsDeletable => IsEditable && !IsK2BoundaryRow;

    /// <summary>K2 行事件列锁定——UI 上 K2 行本就不出事件编辑，仅 K2 语义兜底</summary>
    public bool CanEditEvent => IsEditable && !KeymapRules.IsK2(KeyId);
    public string? EventLockTip => KeymapRules.IsK2(KeyId) ? KeymapRules.K2EventLockReason : null;

    // ---- 修饰键位标志（Ctrl/Shift/Alt/Win ↔ modifier 位表 0x01/02/04/08） ----
    public bool HasCtrl { get => (Modifier & 0x01) != 0; set => SetModifierBit(0x01, value); }
    public bool HasShift { get => (Modifier & 0x02) != 0; set => SetModifierBit(0x02, value); }
    public bool HasAlt { get => (Modifier & 0x04) != 0; set => SetModifierBit(0x04, value); }
    public bool HasWin { get => (Modifier & 0x08) != 0; set => SetModifierBit(0x08, value); }

    private void SetModifierBit(byte mask, bool on)
    {
        var updated = on ? (byte)(Modifier | mask) : (byte)(Modifier & ~mask);
        if (updated == Modifier)
            return;
        Modifier = updated;
        OnPropertyChanged(nameof(ModifierName));
    }

    // ---- 捕获 / 预设写入 ----

    /// <summary>键盘捕获结果（KeyCaptureBox → 行）：修饰键位表 + 单主键 usage。</summary>
    public void ApplyCapturedKey(byte modifier, byte usage)
    {
        Modifier = modifier;
        CodeLo = usage;
        CodeHi = 0;
        CodeEx = 0;
    }

    /// <summary>专用键（F13~F16：物理键盘不存在、系统零冲突；2026-10-15 定稿键值，出厂默认即此四键）</summary>
    public static IReadOnlyList<DedicatedKeyOption> DedicatedKeys { get; } =
    [
        new("F13（语音开·出厂默认）", 0x68),
        new("F14（语音关·出厂默认）", 0x69),
        new("F15（扩音开·出厂默认）", 0x6A),
        new("F16（扩音关·出厂默认）", 0x6B),
    ];

    public sealed record DedicatedKeyOption(string Name, byte Usage);

    [ObservableProperty]
    private DedicatedKeyOption? _selectedDedicatedKey;

    partial void OnSelectedDedicatedKeyChanged(DedicatedKeyOption? value)
    {
        if (value == null)
            return;
        ApplyCapturedKey(0x00, value.Usage);
        SelectedDedicatedKey = null;   // 复位以便重复选择同一项
    }

    /// <summary>消费键预设（Consumer 16bit usage；键盘捕获不到，只能选择）</summary>
    public static IReadOnlyList<ConsumerPresetOption> ConsumerPresets { get; } =
    [
        new("音量 +", 0xE9, 0x00),
        new("音量 −", 0xEA, 0x00),
        new("静音", 0xE2, 0x00),
        new("播放 / 暂停", 0xCD, 0x00),
        new("下一曲", 0xB5, 0x00),
        new("上一曲", 0xB6, 0x00),
    ];

    public sealed record ConsumerPresetOption(string Name, byte CodeLo, byte CodeHi);

    [ObservableProperty]
    private ConsumerPresetOption? _selectedConsumerPreset;

    partial void OnSelectedConsumerPresetChanged(ConsumerPresetOption? value)
    {
        if (value == null)
            return;
        ActionType = KeyActionType.Consumer;
        CodeLo = value.CodeLo;
        CodeHi = value.CodeHi;
        SelectedConsumerPreset = null;
    }

    /// <summary>鼠标键预设（Mouse Btn 报告）</summary>
    public static IReadOnlyList<MouseButtonOption> MouseButtonOptions { get; } =
    [
        new("左键", 0x01),
        new("右键", 0x02),
        new("中键", 0x04),
    ];

    public sealed record MouseButtonOption(string Name, byte Code);

    [ObservableProperty]
    private MouseButtonOption? _selectedMouseButtonPreset;

    partial void OnSelectedMouseButtonPresetChanged(MouseButtonOption? value)
    {
        if (value == null)
            return;
        ActionType = KeyActionType.MouseButton;
        CodeLo = value.Code;
        CodeHi = 0;
        SelectedMouseButtonPreset = null;
    }

    /// <summary>转回协议模型</summary>
    public KeymapEntry ToEntry() => new()
    {
        KeyId = KeyId,
        EventType = EventType,
        ActionType = ActionType,
        Modifier = Modifier,
        CodeLo = CodeLo,
        CodeHi = CodeHi,
        CodeEx = CodeEx,
        Reserved = 0
    };
}
