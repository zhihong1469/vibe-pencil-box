// <copyright file="SyntheticKeyboardSender.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Runtime.InteropServices;
using PencilBox.Core.Models;
using PencilBox.Core.Services;
using PencilBox.Core.SoftTrigger;

namespace PencilBox.Infrastructure.Platform;

/// <summary>
/// <see cref="ISyntheticKeyboard"/> 的 Win32 SendInput 实现（软触发回放发腿 + CLI selftest 注入）。
/// 事件进前台窗口焦点（标准 SendInput 语义）；被钩子看到时带 LLKHF_INJECTED 位——
/// 软触发自身靠动态预留律防回环（触发键∩动作键=∅，SoftTriggerKeys.ValidateGlobal），不依赖注入位过滤。
/// </summary>
public sealed class SyntheticKeyboardSender : ISyntheticKeyboard
{
    private const uint InputKeyboard = 1;
    private const uint KeyeventfKeyup = 0x0002;

    public void SendSequence(IReadOnlyList<SoftTriggerStep> steps)
    {
        foreach (var step in steps)
        {
            SendStep(step);
            if (step.DelayMsAfter > 0)
                Thread.Sleep(step.DelayMsAfter);   // 周期穿插在步间（v2）
        }
    }

    /// <summary>单步：修饰 DOWN → 主键 DOWN/保持/UP → 修饰逆序 UP。</summary>
    private static void SendStep(SoftTriggerStep step)
    {
        var modifierVks = ModifierVks(step.Modifier);
        foreach (var vk in modifierVks)
            SendKey(vk, down: true);

        SendKey(UsageToVirtualKey(step.Usage), down: true);
        Thread.Sleep(30);   // 主键保持（经验值：过短部分程序轮询不到）
        SendKey(UsageToVirtualKey(step.Usage), down: false);

        for (var i = modifierVks.Length - 1; i >= 0; i--)
            SendKey(modifierVks[i], down: false);
    }

    private static int[] ModifierVks(byte modifier)
    {
        var vks = new List<int>(4);
        if ((modifier & 0x01) != 0) vks.Add(0x11);   // VK_CONTROL
        if ((modifier & 0x02) != 0) vks.Add(0x10);   // VK_SHIFT
        if ((modifier & 0x04) != 0) vks.Add(0x12);   // VK_MENU (Alt)
        if ((modifier & 0x08) != 0) vks.Add(0x5B);   // VK_LWIN
        return vks.ToArray();
    }

    /// <summary>HID usage → Win32 VK（与 KeyboardUsageMap.FromVirtualKey 互逆的发送侧；未知 usage 抛异常）。</summary>
    internal static int UsageToVirtualKey(byte usage) => usage switch
    {
        >= 0x04 and <= 0x1D => usage - 0x04 + 0x41,              // A~Z
        >= 0x1E and <= 0x26 => usage - 0x1E + 0x31,              // 1~9
        0x27 => 0x30,                                             // 0
        >= 0x3A and <= 0x45 => usage - 0x3A + 0x70,              // F1~F12
        >= 0x68 and <= 0x73 => usage - 0x68 + 0x7C,              // F13~F24
        0x28 => 0x0D, 0x2C => 0x20, 0x29 => 0x1B, 0x2A => 0x08, 0x2B => 0x09,
        0x39 => 0x14, 0x4C => 0x2E, 0x49 => 0x2D, 0x4A => 0x24, 0x4D => 0x23,
        0x4B => 0x21, 0x4E => 0x22,
        0x50 => 0x25, 0x4F => 0x27, 0x52 => 0x26, 0x51 => 0x28,
        _ => throw new ArgumentException($"未知键值 usage=0x{usage:X2}（发送侧不认识）"),
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
            throw new InvalidOperationException($"SendInput 失败（vk=0x{vk:X2} down={down}，Win32 错误码 {Marshal.GetLastWin32Error()}）");
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
