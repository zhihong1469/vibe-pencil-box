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
    private const uint MouseeventfLeftDown = 0x0002;
    private const uint MouseeventfLeftUp = 0x0004;
    private const uint MouseeventfRightDown = 0x0008;
    private const uint MouseeventfRightUp = 0x0010;

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

    /// <summary>抽象动作 → Win32 VK（None/VoiceBoundary 无 VK，返回 null）。</summary>
    public static int? VkOf(SrxActionKind kind) => kind switch
    {
        SrxActionKind.VolumeUp => VkVolumeUp,
        SrxActionKind.VolumeDown => VkVolumeDown,
        SrxActionKind.PageUp => VkPageUp,
        SrxActionKind.PageDown => VkPageDown,
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
