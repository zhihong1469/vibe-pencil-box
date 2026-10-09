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

    private static string KeyboardUsageName(byte usage) => usage switch
    {
        0x4B => "PageUp",
        0x4E => "PageDown",
        0x3B => "F2",
        0x3E => "F5",
        0x42 => "F9",
        0x43 => "F10",
        0x44 => "F11",
        0x45 => "F12",
        0x28 => "Enter",
        0x29 => "Escape",
        0x2A => "Backspace",
        0x2B => "Tab",
        0xE1 => "Left Shift",
        0x80 => "Volume Up",
        0x81 => "Volume Down",
        _ => $"0x{usage:X2}"
    };

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

    /// <summary>修饰键名</summary>
    public static string GetModifierName(byte modifier) => modifier switch
    {
        0x00 => "—",
        0x01 => "Ctrl",
        0x02 => "Shift",
        0x04 => "Alt",
        0x08 => "Win",
        _ => $"0x{modifier:X2}"
    };

    /// <summary>S1 档位名（byte2 mode 语义，对齐固件 vp_protocol.h 注释与产品定名：待机/识别/扩音）</summary>
    public static string GetModeName(byte mode) => mode switch
    {
        0x00 => "待机",
        0x01 => "识别",
        0x02 => "扩音",
        _ => $"0x{mode:X2}"
    };
}
