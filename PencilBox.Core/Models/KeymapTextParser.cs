// <copyright file="KeymapTextParser.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Protocol;

namespace PencilBox.Core.Models;

/// <summary>
/// Keymap 字段文本解析（纯逻辑，CLI 与测试共用；与 <see cref="KeymapFormatter"/> 成对——一个出、一个进）。
/// </summary>
public static class KeymapTextParser
{
    /// <summary>解析键标识：`K3`/`k3`/`3`/`0x03`；组合键用 `0xF0` 型十六进制。</summary>
    public static byte ParseKeyId(string s)
    {
        s = s.Trim().ToUpperInvariant();
        if (s.StartsWith('K') && s.Length <= 2 && byte.TryParse(s.AsSpan(1), out var k) && k is >= 1 and <= 9)
            return k;
        return ParseByte(s);
    }

    /// <summary>解析事件：`click/double/long/longrel/special`（或数字 1~5）。</summary>
    public static KeyEventType ParseEvent(string s) => s.Trim().ToLowerInvariant() switch
    {
        "click" or "单击" or "1" => KeyEventType.Click,
        "double" or "dclick" or "双击" or "2" => KeyEventType.DoubleClick,
        "long" or "longpress" or "lp" or "长按" or "3" => KeyEventType.LongPress,
        "longrel" or "longrelease" or "长按抬起" or "4" => KeyEventType.LongRelease,
        "special" or "longspecial" or "长按特殊" or "5" => KeyEventType.LongSpecial,
        _ => throw new ArgumentException($"未知事件类型：{s}（可用 click/double/long/longrel/special 或 1~5）"),
    };

    /// <summary>解析动作：`none/keyboard|kb/mouse/consumer|cons/special/notify`（或数字 0~5）。</summary>
    public static KeyActionType ParseAction(string s) => s.Trim().ToLowerInvariant() switch
    {
        "none" or "0" => KeyActionType.None,
        "keyboard" or "kb" or "1" => KeyActionType.Keyboard,
        "mouse" or "2" => KeyActionType.MouseButton,
        "consumer" or "cons" or "3" => KeyActionType.Consumer,
        "special" or "4" => KeyActionType.Special,
        "notify" or "5" => KeyActionType.AppNotify,
        _ => throw new ArgumentException($"未知动作类型：{s}（可用 none/keyboard/mouse/consumer/special/notify 或 0~5）"),
    };

    /// <summary>解析修饰键：`ctrl/shift/alt/gui|win` 任意组合（`+`/`-` 连接，bit 求和）或 0xNN。</summary>
    public static byte ParseModifier(string s)
    {
        s = s.Trim().ToLowerInvariant();
        if (s.StartsWith("0x"))
            return ParseByte(s);

        byte mod = 0;
        foreach (var token in s.Split(['+', '-', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            mod |= token switch
            {
                "ctrl" or "control" => 0x01,
                "shift" => 0x02,
                "alt" => 0x04,
                "gui" or "win" => 0x08,
                _ => throw new InvalidOperationException($"未知修饰键：{token}（可用 ctrl/shift/alt/gui）"),
            };
        }
        return mod;
    }

    /// <summary>解析字节：`0x4B` / `75`。</summary>
    public static byte ParseByte(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return Convert.ToByte(s[2..], 16);
        return byte.Parse(s);
    }
}
