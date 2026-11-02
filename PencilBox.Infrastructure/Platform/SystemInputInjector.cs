// <copyright file="SystemInputInjector.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Runtime.InteropServices;
using PencilBox.Core.Ble.SoftReceiver;

namespace PencilBox.Infrastructure.Platform;

/// <summary>
/// Win32 SendInput 系统级注入：虚拟键 tap + 鼠标键状态，软件接收器键动作发腿
/// （CLI `srx receive/amp --actions` 与后续 GUI 键动作开关共用）。
/// 与 <see cref="SyntheticKeyboardSender"/>（软触发回放，HID usage 空间）分工：
/// 本类工作在 Win32 VK 空间（音量/翻页等消费键无 HID usage 映射），并补鼠标键注入；
/// 注入进前台焦点，属标准 SendInput 语义（与软触发同口径）。
/// </summary>
public sealed class SystemInputInjector
{
    public const int VkVolumeUp = 0xAF;
    public const int VkVolumeDown = 0xAE;
    public const int VkPageUp = 0x21;
    public const int VkPageDown = 0x22;

    private const uint InputKeyboard = 1;
    private const uint InputMouse = 0;
    private const uint KeyeventfKeyup = 0x0002;
    private const uint MouseeventfMove = 0x0001;
    private const uint MouseeventfLeftDown = 0x0002;
    private const uint MouseeventfLeftUp = 0x0004;
    private const uint MouseeventfRightDown = 0x0008;
    private const uint MouseeventfRightUp = 0x0010;
    private const uint MouseeventfWheel = 0x0800;

    /// <summary>虚拟键 tap N 次（down→保持→up；音量键连 tap 即系统连续调量）。</summary>
    public void KeyTap(int vk, int presses = 1, int holdMs = 20)
    {
        for (var i = 0; i < Math.Max(1, presses); i++)
        {
            SendKey(vk, down: true);
            Thread.Sleep(holdMs);
            SendKey(vk, down: false);
            if (i < presses - 1)
                Thread.Sleep(20);
        }
    }

    /// <summary>鼠标键状态注入（left/right 二选一；down=true 按下 false 释放）。</summary>
    public void MouseButton(bool left, bool down)
    {
        var inputs = new INPUT[1];
        inputs[0].Type = InputMouse;
        inputs[0].U.Mi = new MOUSEINPUT
        {
            DwFlags = left
                ? (down ? MouseeventfLeftDown : MouseeventfLeftUp)
                : (down ? MouseeventfRightDown : MouseeventfRightUp),
        };
        if (SendInput(1, inputs, Marshal.SizeOf<INPUT>()) != 1)
            throw new InvalidOperationException(
                $"SendInput(鼠标) 失败（left={left} down={down}，Win32 错误码 {Marshal.GetLastWin32Error()}）");
    }

    /// <summary>相对位移注入（空鼠槽 x/y 直通；s8 相对量，正=右/下）。</summary>
    public void MouseMoveRel(int dx, int dy)
    {
        var inputs = new INPUT[1];
        inputs[0].Type = InputMouse;
        inputs[0].U.Mi = new MOUSEINPUT { Dx = dx, Dy = dy, DwFlags = MouseeventfMove };
        if (SendInput(1, inputs, Marshal.SizeOf<INPUT>()) != 1)
            throw new InvalidOperationException(
                $"SendInput(位移) 失败（dx={dx} dy={dy}，Win32 错误码 {Marshal.GetLastWin32Error()}）");
    }

    /// <summary>滚轮注入（delta 为 WHEEL_DELTA 的倍数；正=前滚）。</summary>
    public void MouseWheel(int delta)
    {
        var inputs = new INPUT[1];
        inputs[0].Type = InputMouse;
        inputs[0].U.Mi = new MOUSEINPUT { MouseData = unchecked((uint)delta), DwFlags = MouseeventfWheel };
        if (SendInput(1, inputs, Marshal.SizeOf<INPUT>()) != 1)
            throw new InvalidOperationException(
                $"SendInput(滚轮) 失败（delta={delta}，Win32 错误码 {Marshal.GetLastWin32Error()}）");
    }

    /// <summary>HID usage tap（Keymap Keyboard 条目；usage→VK 映射见 <see cref="SyntheticKeyboardSender.UsageToVirtualKey"/>，未知 usage 抛异常）。</summary>
    public void TapUsage(byte usage, byte modifier, int taps = 1)
    {
        var vk = SyntheticKeyboardSender.UsageToVirtualKey(usage);
        // 修饰键位 = HID 修饰字节低四位（bit0 Ctrl / bit1 Shift / bit2 Alt / bit3 Win），
        // 与 SyntheticKeyboardSender 的 SoftTrigger 修饰掩码同约定（出厂表 modifier 全 0）。
        var modifiers = new List<int>(4);
        if ((modifier & 0x01) != 0) modifiers.Add(0x11);
        if ((modifier & 0x02) != 0) modifiers.Add(0x10);
        if ((modifier & 0x04) != 0) modifiers.Add(0x12);
        if ((modifier & 0x08) != 0) modifiers.Add(0x5B);

        for (var i = 0; i < Math.Max(1, taps); i++)
        {
            foreach (var m in modifiers)
                SendKey(m, down: true);
            SendKey(vk, down: true);
            Thread.Sleep(20);
            SendKey(vk, down: false);
            for (var j = modifiers.Count - 1; j >= 0; j--)
                SendKey(modifiers[j], down: false);
            if (i < taps - 1)
                Thread.Sleep(20);
        }
    }

    /// <summary>Consumer 键值 → Win32 VK（目前音量族；其余 null=注入侧未支持，调用方日志降级）。</summary>
    public static int? ConsumerVk(byte codeLo, byte codeHi) => (codeLo, codeHi) switch
    {
        (0xE9, _) => VkVolumeUp,    // Consumer Volume Increment
        (0xEA, _) => VkVolumeDown,  // Consumer Volume Decrement
        _ => null,
    };

    private static void SendKey(int vk, bool down)
    {
        var inputs = new INPUT[1];
        inputs[0].Type = InputKeyboard;
        inputs[0].U.Ki = new KEYBDINPUT
        {
            WVk = (short)vk,
            DwFlags = down ? 0u : KeyeventfKeyup,
        };
        if (SendInput(1, inputs, Marshal.SizeOf<INPUT>()) != 1)
            throw new InvalidOperationException(
                $"SendInput 失败（vk=0x{vk:X2} down={down}，Win32 错误码 {Marshal.GetLastWin32Error()}）");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion U;
    }

    // Size 固定 = MOUSEINPUT（x64 32B）：SendInput 的 cbSize 校验按最大成员算，缺了会参数错误
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT Ki;
        [FieldOffset(0)] public MOUSEINPUT Mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public short WVk;
        public short WScan;
        public uint DwFlags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint DwFlags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
