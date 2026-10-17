// <copyright file="KeymapSwitchRowViewModel.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using CommunityToolkit.Mvvm.ComponentModel;
using PencilBox.Core.Models;
using PencilBox.Core.Protocol;

namespace PencilBox.Desktop.ViewModels;

/// <summary>
/// S1 档位切换快捷键行（可编辑）——切换表 `KeymapSwitchEntry`（3B [mode][modifier][code]）的 UI 投影：
/// 档位列锁定（三档=待机/识别/扩音，物理开关位置决定，不可增删），键值可编辑
/// （捕获框/修饰位/专用键 F13~F16，与按键行同交互；切换表只有单码字节，无消费键/鼠标键预设）。
/// 设备写回待 0x55 PARAM 05/06（pc-issues/05）；当前编辑只落本地命名配置，出厂真值兜底。
/// </summary>
public partial class KeymapSwitchRowViewModel : ObservableObject
{
    /// <summary>本行是否可编辑（出厂默认配置整体只读）</summary>
    public bool IsEditable { get; }

    /// <summary>触发档位（锁定：0=待机 1=识别 2=扩音）</summary>
    public byte Mode { get; }

    public string ModeName => KeymapFormatter.GetModeName(Mode);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModifierName))]
    [NotifyPropertyChangedFor(nameof(Raw))]
    private byte _modifier;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CodeName))]
    [NotifyPropertyChangedFor(nameof(Raw))]
    private byte _codeLo;

    public string CodeName => KeymapFormatter.GetCodeName(KeyActionType.Keyboard, CodeLo, 0);
    public string ModifierName => KeymapFormatter.GetModifierName(Modifier);
    public string Raw => string.Join(" ", Mode, $"{Modifier:X2}", $"{CodeLo:X2}");

    public KeymapSwitchRowViewModel(KeymapSwitchEntry entry, bool isEditable = true)
    {
        IsEditable = isEditable;
        Mode = entry.Mode;
        _modifier = entry.Modifier;
        _codeLo = entry.CodeLo;
    }

    public KeymapSwitchRowViewModel(KeymapSwitchEntry entry) : this(entry, isEditable: true) { }

    // ---- 修饰键位标志（与 KeymapRowViewModel 同规则：Ctrl/Shift/Alt/Win ↔ 0x01/02/04/08） ----
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
        OnPropertyChanged(nameof(Raw));
    }

    /// <summary>专用键一键写入（清修饰位——与按键行 ApplyCapturedKey 同语义）。</summary>
    public void ApplyCapturedKey(byte modifier, byte usage)
    {
        Modifier = modifier;
        CodeLo = usage;
    }

    [ObservableProperty]
    private KeymapRowViewModel.DedicatedKeyOption? _selectedDedicatedKey;

    partial void OnSelectedDedicatedKeyChanged(KeymapRowViewModel.DedicatedKeyOption? value)
    {
        if (value == null)
            return;
        ApplyCapturedKey(0x00, value.Usage);
        SelectedDedicatedKey = null;   // 复位以便重复选择同一项
    }

    /// <summary>转回协议模型</summary>
    public KeymapSwitchEntry ToEntry() => new() { Mode = Mode, Modifier = Modifier, CodeLo = CodeLo };
}
