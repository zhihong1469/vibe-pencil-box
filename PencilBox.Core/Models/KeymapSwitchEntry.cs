// <copyright file="KeymapSwitchEntry.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Models;

/// <summary>
/// S1 档位切换快捷键条目——字节级对应固件 `srv_keymap.c` 的 `srv_switch_entry_t`
///（[mode][modifier][code]，3B）：Dongle 检测到笔端 byte2 档位变化时查此表上报热键
///（扩音/识别档位切换边界）。与按键 Keymap 表（<see cref="KeymapEntry"/>）是**两张表**：
/// 按键表走 0xA0 读写（未上线），切换表暂无读写协议（pc-issues/04 §4 扩展待固件裁决），
/// PC 当前只做出厂真值只读显示。
/// </summary>
public class KeymapSwitchEntry
{
    /// <summary>触发档位（byte2 mode 语义）：0=待机(IDLE) 1=识别(CAPTURING) 2=扩音(AMPLIFYING)</summary>
    public byte Mode { get; set; }

    /// <summary>HID 修饰键（bit0 Ctrl / bit1 Shift / bit2 Alt / bit3 Win）</summary>
    public byte Modifier { get; set; }

    /// <summary>HID 键码</summary>
    public byte CodeLo { get; set; }

    /// <summary>3B 原文 [mode][modifier][code]</summary>
    public byte[] ToBytes() => [Mode, Modifier, CodeLo];
}
