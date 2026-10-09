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
/// Keymap 编辑行（UI 模型，包装 KeymapEntry 供 DataGrid 展示/编辑）
/// 与 Core 的 KeymapEntry（协议模型）解耦，编辑后通过 ToEntry() 回写。
/// </summary>
public partial class KeymapRowViewModel : ObservableObject
{
    /// <summary>在接收器表中的索引（0-based）</summary>
    public byte Index { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyName))]
    [NotifyPropertyChangedFor(nameof(EventTypeName))]
    [NotifyPropertyChangedFor(nameof(CanEditEvent))]
    [NotifyPropertyChangedFor(nameof(EventLockTip))]
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

    public KeymapRowViewModel(byte index, KeymapEntry entry)
    {
        Index = index;
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

    /// <summary>K2 行事件列锁定（语音会话边界契约，pc-issues/04 §2A）——UI 按键列本就只读，K2 行保持 K2</summary>
    public bool CanEditEvent => !KeymapRules.IsK2(KeyId);
    public string? EventLockTip => KeymapRules.IsK2(KeyId) ? KeymapRules.K2EventLockReason : null;

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
