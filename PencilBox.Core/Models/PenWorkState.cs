// <copyright file="PenWorkState.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Protocol;

namespace PencilBox.Core.Models;

/// <summary>
/// 笔端工作状态自管的触发键绑定（产品落地/byte2…/PC 落地回执 §三 立案落地，2026-10-18）。
///
/// 依据（终审 §5-2 口径）：byte2 工作模式语义退役后，工作状态权威=事件流——
/// 识别启停=K2 会话边界（0x13 [2][0x03/0x04] → 基站映射热键），扩音档=S1 切换表（0x16 → 热键）。
/// 上位机对基站上报 OS 的热键做全局监听，自维护"识别 × 扩音档"两轴状态。
/// 绑定值**从 Keymap 配置推导**（联动：改 Keymap 表即改触发键，改"默认配置"外的表无需固件参与）。
/// </summary>
public sealed record PenWorkStateBinding(
    byte SessionStartUsage,
    byte SessionEndUsage,
    byte AmpOnUsage,
    byte AmpOffUsage,
    string SourceName)
{
    /// <summary>从 Keymap 配置推导绑定：K2 行（会话开始=LongPress/结束=LongRelease）+ S1 切换表（mode2=扩音开 / mode1=扩音关）。</summary>
    /// <remarks>仅键盘动作（<see cref="KeyActionType.Keyboard"/>）可被键盘钩子观测；
    /// 消费键/鼠标键事件行对自管不可见（对应腿=0，<see cref="IsUsable"/> 为 false 的腿被忽略）。</remarks>
    public static PenWorkStateBinding FromKeymap(
        IEnumerable<KeymapEntry> entries,
        IEnumerable<KeymapSwitchEntry> switchEntries,
        string sourceName)
    {
        byte start = 0, end = 0, ampOn = 0, ampOff = 0;

        foreach (var e in entries)
        {
            if (e.KeyId != KeymapRules.K2KeyId || e.ActionType != KeyActionType.Keyboard)
                continue;
            if (e.EventType == KeyEventType.LongPress)
                start = e.CodeLo;                       // [2][0x03] 会话开始（出厂 F13）
            else if (e.EventType == KeyEventType.LongRelease)
                end = e.CodeLo;                         // [2][0x04] 会话结束（出厂 F14）
        }

        foreach (var s in switchEntries)
        {
            if (s.Modifier != 0)
                continue;                               // 带修饰键的组合不参与（钩子只匹配裸主键）
            if (s.Mode == 0x02)
                ampOn = s.CodeLo;                       // SPK 档=扩音开（出厂 F15）
            else if (s.Mode == 0x01)
                ampOff = s.CodeLo;                      // ON 档=扩音关（出厂 F16）
        }

        return new PenWorkStateBinding(start, end, ampOn, ampOff, sourceName);
    }

    /// <summary>四条腿是否齐备（任一腿缺失=该语义不参与推导）。</summary>
    public bool IsUsable => SessionStartUsage != 0 && SessionEndUsage != 0 && AmpOnUsage != 0 && AmpOffUsage != 0;
}

/// <summary>
/// 笔端工作状态机（纯逻辑，可单测）：吃触发键 usage 边沿，维护 识别 × 扩音档 两轴。
/// 未知（null）起步——PC 重启后历史按键追不回来，等下一次边界事件自愈；
/// 断连即清零（<see cref="Reset"/>），重连靠 0x16 连接补报（扩音档）+ 下一次 K2 边界（识别）恢复。
/// </summary>
public sealed class PenWorkStateTracker
{
    private readonly PenWorkStateBinding _binding;
    private bool? _sessionActive;
    private bool? _ampActive;

    public PenWorkStateTracker(PenWorkStateBinding binding) => _binding = binding;

    /// <summary>识别会话是否进行中（null=未知）。</summary>
    public bool? SessionActive => _sessionActive;

    /// <summary>扩音档是否活跃（null=未知）。</summary>
    public bool? AmpActive => _ampActive;

    /// <summary>是否已至少见过一次边界事件。</summary>
    public bool IsKnown => _sessionActive != null || _ampActive != null;

    public PenWorkStateBinding Binding => _binding;

    /// <summary>
    /// 喂一次按键（HID usage 主键；键盘钩子 down 边沿）。命中绑定键即转移；返回状态是否变化。
    /// </summary>
    public bool Feed(byte usage)
    {
        if (usage == 0)
            return false;
        if (usage == _binding.SessionStartUsage && _sessionActive != true)
        {
            _sessionActive = true;
            return true;
        }
        if (usage == _binding.SessionEndUsage && _sessionActive != false)
        {
            _sessionActive = false;
            return true;
        }
        if (usage == _binding.AmpOnUsage && _ampActive != true)
        {
            _ampActive = true;
            return true;
        }
        if (usage == _binding.AmpOffUsage && _ampActive != false)
        {
            _ampActive = false;
            return true;
        }
        return false;
    }

    /// <summary>清零回未知（断连/换绑定）。</summary>
    public void Reset()
    {
        _sessionActive = null;
        _ampActive = null;
    }

    /// <summary>展示文本（状态卡/CLI 共用；"常态"=两轴均明确为否）。</summary>
    public string DisplayText => (_sessionActive, _ampActive) switch
    {
        (null, null) => "待事件",
        (true, true) => "识别中 · 扩音档",
        (true, _) => "识别中",
        (_, true) => "扩音档",
        _ => "常态"
    };
}

/// <summary>
/// Win32 虚键码 → USB HID 键盘 usage 换算（键盘钩子消费用）。
/// 覆盖 F1~F24 / 字母 / 数字 / 常用控制键；查不到返回 0（该键不可参与联动——
/// Keymap 里建议联动键选 F 区/字母/数字，与 KeyCaptureBox 可录入的 usage 空间对齐）。
/// </summary>
public static class KeyboardUsageMap
{
    /// <summary>VK → HID usage；不认识返回 0x00。</summary>
    public static byte FromVirtualKey(int vk) => vk switch
    {
        >= 0x30 and <= 0x39 => vk == 0x30 ? (byte)0x27 : (byte)(vk - 0x13),   // '0'~'9'（'0' 在排尾）
        >= 0x41 and <= 0x5A => (byte)(vk - 0x3D),                             // 'A'~'Z'
        >= 0x70 and <= 0x7B => (byte)(vk - 0x36),                             // F1~F12
        >= 0x7C and <= 0x87 => (byte)(vk - 0x14),                             // F13~F24
        0x08 => (byte)0x2A,                                                   // Backspace
        0x09 => (byte)0x2B,                                                   // Tab
        0x0D => (byte)0x28,                                                   // Enter
        0x14 => (byte)0x39,                                                   // CapsLock
        0x1B => (byte)0x29,                                                   // Escape
        0x20 => (byte)0x2C,                                                   // Space
        0x25 => (byte)0x50,                                                   // ←
        0x26 => (byte)0x52,                                                   // ↑
        0x27 => (byte)0x4F,                                                   // →
        0x28 => (byte)0x51,                                                   // ↓
        _ => 0x00,
    };
}
