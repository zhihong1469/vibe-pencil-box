// <copyright file="KeymapFormatter.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Protocol;

namespace PencilBox.Core.Models;

/// <summary>
/// Keymap 显示格式化（纯逻辑，无 UI 依赖，可单测）
/// 把 KeymapEntry 的字节字段转成友好名称，供 UI 展示。
/// 对齐 link/custom-hid/now/01_Keymap出厂默认映射表.md §5
/// </summary>
public static class KeymapFormatter
{
    /// <summary>物理键名（key_id → 产品名）</summary>
    public static string GetKeyName(byte keyId) => keyId switch
    {
        0x01 => "K1 飞鼠",
        0x02 => "K2 语音",
        0x03 => "K3 前翻",
        0x04 => "K4 后翻",
        0x05 => "K5 功能",
        0x06 => "K6",               // 固件出厂表后来加的音量−键（srv_keymap.c 注释就叫 K6；产品描述名未定，勿自造）
        >= 0xF0 => $"组合键 0x{keyId:X2}",
        _ => $"键 0x{keyId:X2}"
    };

    /// <summary>事件类型名</summary>
    public static string GetEventTypeName(KeyEventType eventType) => eventType switch
    {
        KeyEventType.Click => "单击",
        KeyEventType.DoubleClick => "双击",
        KeyEventType.LongPress => "长按",
        KeyEventType.LongRelease => "长按抬起",
        KeyEventType.LongSpecial => "长按特殊",
        _ => $"0x{(byte)eventType:X2}"
    };

    /// <summary>
    /// 事件显示名（按键感知）：K2 的 0x03/0x04 真实语义是"语音会话开始/结束边界"
    /// （笔端两种启动方式归一上报，方式1 下由单击触发），按作用描述而非按键力学（pc-issues/04 §2A）。
    /// </summary>
    public static string GetEventDisplay(byte keyId, KeyEventType eventType) =>
        KeymapRules.IsK2(keyId)
            ? eventType switch
            {
                KeyEventType.LongPress => "会话开始",
                KeyEventType.LongRelease => "会话结束",
                _ => GetEventTypeName(eventType),
            }
            : GetEventTypeName(eventType);

    /// <summary>动作类型名</summary>
    public static string GetActionTypeName(KeyActionType actionType) => actionType switch
    {
        KeyActionType.None => "无动作",
        KeyActionType.Keyboard => "键盘",
        KeyActionType.MouseButton => "鼠标按键",
        KeyActionType.Consumer => "消费键",
        KeyActionType.Special => "特殊功能",
        KeyActionType.AppNotify => "应用通知",
        _ => $"0x{(byte)actionType:X2}"
    };

    /// <summary>键值友好名（按 action_type 解释 code）</summary>
    public static string GetCodeName(KeyActionType actionType, byte codeLo, byte codeHi)
    {
        ushort code = (ushort)((codeHi << 8) | codeLo);

        return actionType switch
        {
            KeyActionType.Keyboard => KeyboardUsageName(codeLo),
            KeyActionType.Consumer => ConsumerUsageName(code),
            KeyActionType.MouseButton => MouseButtonName(codeLo),
            KeyActionType.Special => SpecialName(codeLo),
            KeyActionType.None => "—",
            _ => $"0x{code:X4}"
        };
    }

    private static string KeyboardUsageName(byte usage)
    {
        // 连续段直接算：字母 A~Z（0x04~0x1D）、数字 1~0（0x1E~0x27）、F1~F12（0x3A~0x45）、F13~F24（0x68~0x73）
        if (usage is >= 0x04 and <= 0x1D) return ((char)('A' + usage - 0x04)).ToString();
        if (usage is >= 0x1E and <= 0x26) return ((char)('1' + usage - 0x1E)).ToString();
        if (usage == 0x27) return "0";
        if (usage is >= 0x3A and <= 0x45) return $"F{usage - 0x3A + 1}";
        if (usage is >= 0x68 and <= 0x73) return $"F{usage - 0x68 + 13}";

        return usage switch
        {
            0x28 => "Enter",
            0x29 => "Escape",
            0x2A => "Backspace",
            0x2B => "Tab",
            0x2C => "Space",
            0x2D => "-",
            0x2E => "=",
            0x2F => "[",
            0x30 => "]",
            0x31 => "\\",
            0x33 => ";",
            0x34 => "'",
            0x35 => "`",
            0x36 => ",",
            0x37 => ".",
            0x38 => "/",
            0x39 => "CapsLock",
            0x46 => "PrintScreen",
            0x47 => "ScrollLock",
            0x48 => "Pause",
            0x49 => "Insert",
            0x4A => "Home",
            0x4B => "PageUp",
            0x4C => "Delete",
            0x4D => "End",
            0x4E => "PageDown",
            0x4F => "→",
            0x50 => "←",
            0x51 => "↓",
            0x52 => "↑",
            0x53 => "NumLock",
            _ => $"0x{usage:X2}"
        };
    }

    private static string ConsumerUsageName(ushort usage) => usage switch
    {
        0x0223 => "AC Home",
        0x00E9 => "Volume Up",
        0x00EA => "Volume Down",
        0x00CD => "Play/Pause",
        0x00E2 => "Mute",
        _ => $"0x{usage:X4}"
    };

    private static string MouseButtonName(byte code) => code switch
    {
        0x01 => "左键",
        0x02 => "右键",
        0x04 => "中键",
        _ => $"0x{code:X2}"
    };

    private static string SpecialName(byte code) => code switch
    {
        0x01 => "飞鼠开关",
        0x02 => "语音启停",
        _ => $"SPECIAL 0x{code:X2}"
    };

    /// <summary>修饰键名（支持组合位表；bit0=Ctrl bit1=Shift bit2=Alt bit3=Win）</summary>
    public static string GetModifierName(byte modifier)
    {
        if (modifier == 0x00)
            return "—";

        var parts = new List<string>(4);
        if ((modifier & 0x01) != 0) parts.Add("Ctrl");
        if ((modifier & 0x02) != 0) parts.Add("Shift");
        if ((modifier & 0x04) != 0) parts.Add("Alt");
        if ((modifier & 0x08) != 0) parts.Add("Win");

        var name = string.Join("+", parts);
        return (modifier & 0xF0) != 0 ? $"{name}+0x{modifier >> 4:X2}" : name;
    }

    /// <summary>S1 档位名（byte2 mode 语义，对齐固件 vp_protocol.h 注释与产品定名：待机/识别/扩音）</summary>
    public static string GetModeName(byte mode) => mode switch
    {
        0x00 => "待机",
        0x01 => "识别",
        0x02 => "扩音",
        _ => $"0x{mode:X2}"
    };
}
