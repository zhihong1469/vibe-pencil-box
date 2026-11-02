// <copyright file="SoftTriggerTextParser.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.SoftTrigger;

namespace PencilBox.Core.Models;

/// <summary>
/// 软触发序列文本解析（13 号；CLI --keys 与测试共用，与 <see cref="SoftTriggerDescriber"/> 成对）。
/// 语法：逗号分步；步内 `修饰+主键[@步前延时ms]`，如 `F2`、`ALT+L`、`F2@100`、`CTRL+SHIFT+S@250`
/// （@ms=本步回放【前】等待，v2.3 起步前语义——触发源后第一步设延时可防回放键夹进触发键按压）。
/// 主键：A~Z / 0~9 / F1~F24 / ENTER SPACE ESC TAB BACKSPACE DELETE INSERT HOME END PGUP PGDN 方向键。
/// v2.4：触发键池=全键盘含 F13~F16（工作状态占用由 UI 标注）；修饰键支持左右侧别
/// `LCTRL/RCTRL/LSHIFT/RSHIFT/LALT/RALT/LWIN/RWIN`（物理键盘侧别精确匹配；设备注入只有通用位）。
/// </summary>
public static class SoftTriggerTextParser
{
    /// <summary>解析整串（"F2,ALT+L" → 两步）；语法错抛 <see cref="ArgumentException"/>。</summary>
    public static List<SoftTriggerStep> ParseSequence(string text)
    {
        var steps = new List<SoftTriggerStep>();
        foreach (var raw in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            steps.Add(ParseStep(raw));
        }
        if (steps.Count == 0)
            throw new ArgumentException("序列为空：例 --keys \"F2,ALT+L\"");
        return steps;
    }

    /// <summary>解析单步（"ALT+L" / "LCTRL+A" / "F2@100"——@后为步前延时 ms：本步回放前等待）。</summary>
    public static SoftTriggerStep ParseStep(string text)
    {
        ushort modifier = 0;
        string main = text.Trim();

        // @步前延时：主键后缀 "F2@100"
        int delay = 0;
        var at = main.LastIndexOf('@');
        if (at >= 0)
        {
            var delayText = main[(at + 1)..].Trim();
            if (!int.TryParse(delayText, out delay) || delay < 0)
                throw new ArgumentException($"步前延时非法（@后须为非负整数 ms）：「{text}」");
            main = main[..at].Trim();
        }

        var parts = main.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            throw new ArgumentException($"空键步：「{text}」");

        for (var i = 0; i < parts.Length - 1; i++)
        {
            modifier |= parts[i].ToLowerInvariant() switch
            {
                "ctrl" or "control" => SoftTriggerModifiers.Ctrl,
                "shift" => SoftTriggerModifiers.Shift,
                "alt" => SoftTriggerModifiers.Alt,
                "win" or "gui" or "windows" => SoftTriggerModifiers.Win,
                "lctrl" or "lcontrol" => SoftTriggerModifiers.LCtrl,
                "rctrl" or "rcontrol" => SoftTriggerModifiers.RCtrl,
                "lshift" => SoftTriggerModifiers.LShift,
                "rshift" => SoftTriggerModifiers.RShift,
                "lalt" => SoftTriggerModifiers.LAlt,
                "ralt" => SoftTriggerModifiers.RAlt,
                "lwin" => SoftTriggerModifiers.LWin,
                "rwin" => SoftTriggerModifiers.RWin,
                _ => throw new ArgumentException(
                    $"未知修饰键「{parts[i]}」（可用 ctrl/shift/alt/win 或 l/r 侧别前缀 lctrl/rshift…）：{text}"),
            };
        }

        var usage = MainKeyUsage(parts[^1]);
        return new SoftTriggerStep(usage, modifier, delay);
    }

    /// <summary>解析触发键（"ALT+F2" / "RSHIFT+F5" → (usage, modifier)；裸键修饰=0）。</summary>
    public static (byte Usage, ushort Modifier) ParseTrigger(string text)
    {
        var step = ParseStep(text);
        return (step.Usage, step.Modifier);
    }

    /// <summary>主键名 → HID usage（与 KeyboardUsageMap 同一张表的文字面）。</summary>
    public static byte MainKeyUsage(string name)
    {
        var t = name.Trim().ToUpperInvariant();

        if (t.Length == 1)
        {
            var c = t[0];
            if (c is >= 'A' and <= 'Z') return (byte)(0x04 + c - 'A');
            if (c is >= '1' and <= '9') return (byte)(0x1D + c - '1');
            if (c == '0') return 0x27;
        }
        if (t.Length >= 2 && t[0] == 'F' && int.TryParse(t[1..], out var f) && f is >= 1 and <= 24)
            return (byte)(f <= 12 ? 0x3A + f - 1 : 0x68 + f - 13);

        return t switch
        {
            "ENTER" or "RETURN" or "回车" => 0x28,
            "SPACE" or "空格" => 0x2C,
            "ESC" or "ESCAPE" => 0x29,
            "TAB" => 0x2B,
            "BACKSPACE" or "BKSP" => 0x2A,
            "DELETE" or "DEL" => 0x4C,
            "INSERT" or "INS" => 0x49,
            "HOME" => 0x4A,
            "END" => 0x4D,
            "PAGEUP" or "PGUP" => 0x4B,
            "PAGEDOWN" or "PGDN" => 0x4E,
            "LEFT" or "←" => 0x50,
            "RIGHT" or "→" => 0x4F,
            "UP" or "↑" => 0x52,
            "DOWN" or "↓" => 0x51,
            _ => throw new ArgumentException($"未知主键「{name}」（例：A / 5 / F2 / ALT+L / CTRL+SHIFT+S）"),
        };
    }
}

/// <summary>步骤/定义 → 可读文本（出方向；GUI 列表与 CLI 回显共用；修饰文本走 <see cref="SoftTriggerModifiers"/> 单点，侧别=左Ctrl/右Shift）。</summary>
public static class SoftTriggerDescriber
{
    public static string DescribeStep(SoftTriggerStep step)
    {
        var mod = SoftTriggerModifiers.Describe(step.Modifier);
        var text = mod.Length == 0 ? MainKeyName(step.Usage) : $"{mod}+{MainKeyName(step.Usage)}";
        return step.DelayMsBefore > 0 ? $"{text}@{step.DelayMsBefore}" : text;
    }

    /// <summary>触发键可读文本（修饰+键名，如 "Alt+F2"、"左Ctrl+F5"；与 ParseTrigger 互逆）。</summary>
    public static string DescribeTrigger(byte usage, ushort modifier)
    {
        var mod = SoftTriggerModifiers.Describe(modifier);
        return mod.Length == 0 ? MainKeyName(usage) : $"{mod}+{MainKeyName(usage)}";
    }

    public static string DescribeSequence(IReadOnlyList<SoftTriggerStep> steps) =>
        string.Join(" → ", steps.Select(DescribeStep));

    /// <summary>usage → 键名（与解析表互逆；不认识的出 hex）。</summary>
    public static string MainKeyName(byte usage)
    {
        if (usage is >= 0x04 and <= 0x1D) return ((char)('A' + usage - 0x04)).ToString();
        if (usage is >= 0x1E and <= 0x26) return ((char)('1' + usage - 0x1E)).ToString();
        if (usage == 0x27) return "0";
        if (usage is >= 0x3A and <= 0x45) return $"F{usage - 0x3A + 1}";
        if (usage is >= 0x68 and <= 0x73) return $"F{usage - 0x68 + 13}";
        return usage switch
        {
            0x28 => "Enter",
            0x2C => "Space",
            0x29 => "Esc",
            0x2A => "Backspace",
            0x2B => "Tab",
            0x4C => "Delete",
            0x49 => "Insert",
            0x4A => "Home",
            0x4D => "End",
            0x4B => "PageUp",
            0x4E => "PageDown",
            0x50 => "←",
            0x4F => "→",
            0x52 => "↑",
            0x51 => "↓",
            _ => $"0x{usage:X2}",
        };
    }
}
