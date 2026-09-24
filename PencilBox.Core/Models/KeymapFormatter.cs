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
}
