// <copyright file="HidReportDecoder.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Collections.Generic;

namespace PencilBox.Core.Communication;

/// <summary>
/// HID 报告解码器：把 HID 报告字节数组解码成人类可读文本
/// </summary>
public static class HidReportDecoder
{
    /// <summary>
    /// 解码键盘 HID 报告（Report ID 0x01，标准 8 字节）
    /// 字节 0：修饰键位掩码
    /// 字节 1：保留
    /// 字节 2-7：最多 6 个同时按下的键的 HID Usage ID
    /// </summary>
    public static string DecodeKeyboardReport(byte[] data)
    {
        if (data == null || data.Length < 8)
            return "<invalid keyboard report>";

        var modifiers = DecodeModifierKeys(data[0]);
        var pressedKeys = new List<string>();

        for (int i = 2; i < data.Length; i++)
        {
            byte keyCode = data[i];
            if (keyCode == 0) continue;
            pressedKeys.Add(GetKeyName(keyCode));
        }

        if (string.IsNullOrEmpty(modifiers) && pressedKeys.Count == 0)
            return "(no keys pressed)";

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(modifiers)) parts.Add(modifiers);
        parts.AddRange(pressedKeys);

        return string.Join(" + ", parts);
    }

    /// <summary>
    /// 解码 Consumer Control 报告（Report ID 0x02，3 字节，2 字节 Usage ID）
    /// </summary>
    public static string DecodeConsumerReport(byte[] data)
    {
        if (data == null || data.Length < 2)
            return "<invalid consumer report>";

        // 16-bit usage id (little-endian)
        ushort usage = (ushort)(data[0] | (data[1] << 8));

        return usage switch
        {
            0x00B5 => "Scan Next Track",
            0x00B6 => "Scan Previous Track",
            0x00B7 => "Stop",
            0x00B8 => "Eject",
            0x00CD => "Play/Pause",
            0x00E2 => "Mute",
            0x00E9 => "Volume Up",
            0x00EA => "Volume Down",
            0x0150 => "Home",
            0x0223 => "WWW Search",
            0x0224 => "WWW Back",
            0x0225 => "WWW Forward",
            0x0226 => "WWW Stop",
            0x0227 => "WWW Refresh",
            0x022A => "WWW Favorites",
            _ => $"Consumer 0x{usage:X4}"
        };
    }

    /// <summary>
    /// 解码鼠标 HID 报告（Report ID 0x03，5 字节）
    /// 字节 0：按键位掩码
    /// 字节 1-2：X 位移（有符号 16 位）
    /// 字节 3-4：Y 位移（有符号 16 位）
    /// </summary>
    public static string DecodeMouseReport(byte[] data)
    {
        if (data == null || data.Length < 5)
            return "<invalid mouse report>";

        byte buttons = data[0];
        short x = (short)(data[1] | (data[2] << 8));
        short y = (short)(data[3] | (data[4] << 8));

        var btnList = new List<string>();
        if ((buttons & 0x01) != 0) btnList.Add("L");
        if ((buttons & 0x02) != 0) btnList.Add("R");
        if ((buttons & 0x04) != 0) btnList.Add("M");
        if ((buttons & 0x08) != 0) btnList.Add("Back");
        if ((buttons & 0x10) != 0) btnList.Add("Forward");

        var buttonsText = btnList.Count > 0 ? string.Join("+", btnList) : "-";
        return $"Buttons: {buttonsText} | X: {x:+#;-#;0} | Y: {y:+#;-#;0}";
    }

    /// <summary>
    /// 自动按 Report ID 解码
    /// </summary>
    public static string DecodeAuto(byte reportId, byte[] data)
    {
        return reportId switch
        {
            0x01 => "Keyboard: " + DecodeKeyboardReport(data),
            0x02 => "Consumer: " + DecodeConsumerReport(data),
            0x03 => "Mouse: " + DecodeMouseReport(data),
            _ => $"Report 0x{reportId:X2}: {BytesToHex(data)}"
        };
    }

    /// <summary>
    /// 字节数组转 HEX 字符串
    /// </summary>
    public static string BytesToHex(byte[] data, string separator = " ")
    {
        if (data == null || data.Length == 0)
            return string.Empty;

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < data.Length; i++)
        {
            if (i > 0) sb.Append(separator);
            sb.Append(data[i].ToString("X2"));
        }
        return sb.ToString();
    }

    /// <summary>
    /// 按 Report ID 自动识别
    /// </summary>
    public static string GetReportDescription(byte reportId)
    {
        return reportId switch
        {
            0x01 => "Keyboard",
            0x02 => "Consumer",
            0x03 => "Mouse",
            0x04 => "Audio",
            0x05 => "AudioControl",
            0xA0 => "CustomCommand",
            _ => $"Unknown(0x{reportId:X2})"
        };
    }

    private static string DecodeModifierKeys(byte b)
    {
        var list = new List<string>();
        if ((b & 0x01) != 0) list.Add("LCtrl");
        if ((b & 0x02) != 0) list.Add("LShift");
        if ((b & 0x04) != 0) list.Add("LAlt");
        if ((b & 0x08) != 0) list.Add("LGUI");
        if ((b & 0x10) != 0) list.Add("RCtrl");
        if ((b & 0x20) != 0) list.Add("RShift");
        if ((b & 0x40) != 0) list.Add("RAlt");
        if ((b & 0x80) != 0) list.Add("RGUI");
        return string.Join("+", list);
    }

    private static string GetKeyName(byte usageId)
    {
        return KeyNames.TryGetValue(usageId, out var name) ? name : $"0x{usageId:X2}";
    }

    private static readonly Dictionary<byte, string> KeyNames = new()
    {
        [0x04] = "A", [0x05] = "B", [0x06] = "C", [0x07] = "D", [0x08] = "E",
        [0x09] = "F", [0x0A] = "G", [0x0B] = "H", [0x0C] = "I", [0x0D] = "J",
        [0x0E] = "K", [0x0F] = "L", [0x10] = "M", [0x11] = "N", [0x12] = "O",
        [0x13] = "P", [0x14] = "Q", [0x15] = "R", [0x16] = "S", [0x17] = "T",
        [0x18] = "U", [0x19] = "V", [0x1A] = "W", [0x1B] = "X", [0x1C] = "Y",
        [0x1D] = "Z", [0x1E] = "1", [0x1F] = "2", [0x20] = "3", [0x21] = "4",
        [0x22] = "5", [0x23] = "6", [0x24] = "7", [0x25] = "8", [0x26] = "9",
        [0x27] = "0",
        [0x28] = "Enter", [0x29] = "Esc", [0x2A] = "Backspace", [0x2B] = "Tab",
        [0x2C] = "Space", [0x2D] = "-", [0x2E] = "=", [0x2F] = "[", [0x30] = "]",
        [0x31] = "\\", [0x33] = ";", [0x34] = "'", [0x35] = "`", [0x36] = ",",
        [0x37] = ".", [0x38] = "/",
        [0x39] = "CapsLock",
        [0x3A] = "F1", [0x3B] = "F2", [0x3C] = "F3", [0x3D] = "F4",
        [0x3E] = "F5", [0x3F] = "F6", [0x40] = "F7", [0x41] = "F8",
        [0x42] = "F9", [0x43] = "F10", [0x44] = "F11", [0x45] = "F12",
        [0x46] = "PrintScreen", [0x47] = "ScrollLock", [0x48] = "Pause",
        [0x49] = "Insert", [0x4A] = "Home", [0x4B] = "PageUp",
        [0x4C] = "Delete", [0x4D] = "End", [0x4E] = "PageDown",
        [0x4F] = "Right", [0x50] = "Left", [0x51] = "Down", [0x52] = "Up",
        [0x53] = "NumLock", [0x54] = "KP/", [0x55] = "KP*",
        [0x56] = "KP-", [0x57] = "KP+", [0x58] = "KPEnter",
        [0x59] = "KP1", [0x5A] = "KP2", [0x5B] = "KP3",
        [0x5C] = "KP4", [0x5D] = "KP5", [0x5E] = "KP6",
        [0x5F] = "KP7", [0x60] = "KP8", [0x61] = "KP9",
        [0x62] = "KP0", [0x63] = "KP.",
        [0xE0] = "LCtrl", [0xE1] = "LShift", [0xE2] = "LAlt", [0xE3] = "LGUI",
        [0xE4] = "RCtrl", [0xE5] = "RShift", [0xE6] = "RAlt", [0xE7] = "RGUI",
    };
}