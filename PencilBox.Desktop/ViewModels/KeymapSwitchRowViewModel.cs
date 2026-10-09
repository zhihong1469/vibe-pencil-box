// <copyright file="KeymapSwitchRowViewModel.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Models;
using PencilBox.Core.Protocol;

namespace PencilBox.Desktop.ViewModels;

/// <summary>
/// S1 档位切换快捷键行（只读展示）——出厂切换表 `KeymapFactoryDefault.SwitchEntries`
/// 的 UI 投影；切换表暂无读写协议（pc-issues/04 §4），全配置共用同一份出厂真值。
/// </summary>
public class KeymapSwitchRowViewModel
{
    public KeymapSwitchRowViewModel(KeymapSwitchEntry entry)
    {
        Mode = entry.Mode;
        Modifier = entry.Modifier;
        CodeLo = entry.CodeLo;
    }

    public byte Mode { get; }
    public byte Modifier { get; }
    public byte CodeLo { get; }

    public string ModeName => KeymapFormatter.GetModeName(Mode);
    public string ActionName => KeymapFormatter.GetActionTypeName(KeyActionType.Keyboard);
    public string CodeName => KeymapFormatter.GetCodeName(KeyActionType.Keyboard, CodeLo, 0);
    public string ModifierName => KeymapFormatter.GetModifierName(Modifier);
    public string Raw => string.Join(" ", ToBytes().Select(b => $"{b:X2}"));

    private byte[] ToBytes() => [Mode, Modifier, CodeLo];
}
