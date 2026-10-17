// <copyright file="KeyCaptureBox.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PencilBox.Core.Models;
using PencilBox.Core.Protocol;

namespace PencilBox.Desktop.Controls;

/// <summary>
/// 快捷键捕获框：获得焦点后按下组合键即完成捕获（修饰键 + 单主键），Esc 清除。
/// 只读文本框，不接受手工输入；产物 = HID 修饰键位表 + 键盘 usage 单字节，
/// 经 CapturedModifier / CapturedUsage 两个依赖属性与行模型双向绑定。
/// 物理键盘不存在的键（F13~F16 等）捕获不到——UI 另配"专用键"下拉；
/// 媒体键属 Consumer 页，提示走"消费键"下拉。
/// </summary>
public class KeyCaptureBox : TextBox
{
    public static readonly DependencyProperty CapturedModifierProperty =
        DependencyProperty.Register(nameof(CapturedModifier), typeof(byte), typeof(KeyCaptureBox),
            new FrameworkPropertyMetadata(default(byte),
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnCapturedChanged));

    public static readonly DependencyProperty CapturedUsageProperty =
        DependencyProperty.Register(nameof(CapturedUsage), typeof(byte), typeof(KeyCaptureBox),
            new FrameworkPropertyMetadata(default(byte),
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnCapturedChanged));

    public byte CapturedModifier
    {
        get => (byte)GetValue(CapturedModifierProperty);
        set => SetValue(CapturedModifierProperty, value);
    }

    public byte CapturedUsage
    {
        get => (byte)GetValue(CapturedUsageProperty);
        set => SetValue(CapturedUsageProperty, value);
    }

    public KeyCaptureBox()
    {
        IsReadOnly = true;
        Cursor = Cursors.Hand;
        GotKeyboardFocus += (_, _) => RefreshText();
        LostKeyboardFocus += (_, _) => RefreshText();
        RefreshText();
    }

    private static void OnCapturedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((KeyCaptureBox)d).RefreshText();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Esc：清除捕获
        if (e.Key == Key.Escape)
        {
            SetCurrentValue(CapturedModifierProperty, (byte)0);
            SetCurrentValue(CapturedUsageProperty, (byte)0);
            e.Handled = true;
            return;
        }

        // 修饰键单按：不完成捕获，仅刷新提示
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            RefreshText();
            e.Handled = true;
            return;
        }

        // 媒体键属 Consumer 用法页，键盘捕获语义不对，指路下拉
        if (e.Key is Key.VolumeUp or Key.VolumeDown or Key.VolumeMute
            or Key.MediaPlayPause or Key.MediaNextTrack or Key.MediaPreviousTrack or Key.MediaStop)
        {
            Text = "媒体键请用「消费键」下拉选择";
            e.Handled = true;
            return;
        }

        var usage = MapUsage(e.Key);
        if (usage == null)
        {
            Text = "不支持该键（特殊键值可用 CLI keymap set 录入）";
            e.Handled = true;
            return;
        }

        SetCurrentValue(CapturedModifierProperty, ModifierFromKeyboard());
        SetCurrentValue(CapturedUsageProperty, usage);
        e.Handled = true;
    }

    private void RefreshText()
    {
        if (CapturedUsage == 0)
        {
            Text = IsKeyboardFocused ? "按下组合键（Esc 清除）…" : "点击后按下快捷键…";
            return;
        }
        Text = IsKeyboardFocused ? $"{Describe()}（再按其他键可改，Esc 清除）" : Describe();
    }

    private string Describe()
    {
        var parts = new List<string>(5);
        if ((CapturedModifier & 0x01) != 0) parts.Add("Ctrl");
        if ((CapturedModifier & 0x02) != 0) parts.Add("Shift");
        if ((CapturedModifier & 0x04) != 0) parts.Add("Alt");
        if ((CapturedModifier & 0x08) != 0) parts.Add("Win");
        parts.Add(KeymapFormatter.GetCodeName(KeyActionType.Keyboard, CapturedUsage, 0));
        return string.Join("+", parts);
    }

    private static byte ModifierFromKeyboard()
    {
        var mods = Keyboard.Modifiers;
        byte m = 0;
        if (mods.HasFlag(ModifierKeys.Control)) m |= 0x01;
        if (mods.HasFlag(ModifierKeys.Shift)) m |= 0x02;
        if (mods.HasFlag(ModifierKeys.Alt)) m |= 0x04;
        if (mods.HasFlag(ModifierKeys.Windows)) m |= 0x08;
        return m;
    }

    /// <summary>WPF Key → USB HID 键盘 usage（Keyboard/Keypad 页 0x07）。</summary>
    private static byte? MapUsage(Key key) => key switch
    {
        >= Key.A and <= Key.Z => (byte)(0x04 + (key - Key.A)),
        Key.D0 => 0x27,
        >= Key.D1 and <= Key.D9 => (byte)(0x1D + (key - Key.D0)),
        Key.NumPad0 => 0x62,
        >= Key.NumPad1 and <= Key.NumPad9 => (byte)(0x58 + (key - Key.NumPad0)),
        >= Key.F1 and <= Key.F12 => (byte)(0x3A + (key - Key.F1)),
        >= Key.F13 and <= Key.F24 => (byte)(0x68 + (key - Key.F13)),
        Key.Space => 0x2C,
        Key.Enter or Key.Return => 0x28,
        Key.Tab => 0x2B,
        Key.Back => 0x2A,
        Key.PageUp or Key.Prior => 0x4B,
        Key.PageDown or Key.Next => 0x4E,
        Key.Home => 0x4A,
        Key.End => 0x4D,
        Key.Insert => 0x49,
        Key.Delete => 0x4C,
        Key.Up => 0x52,
        Key.Down => 0x51,
        Key.Left => 0x50,
        Key.Right => 0x4F,
        Key.OemSemicolon => 0x33,
        Key.OemPlus => 0x2E,
        Key.OemComma => 0x36,
        Key.OemMinus => 0x2D,
        Key.OemPeriod => 0x37,
        Key.OemQuestion => 0x38,
        Key.OemTilde => 0x35,
        Key.OemOpenBrackets => 0x2F,
        Key.OemCloseBrackets => 0x30,
        Key.OemPipe => 0x31,
        Key.OemQuotes => 0x34,
        _ => null,
    };
}
