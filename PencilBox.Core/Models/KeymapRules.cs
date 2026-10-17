// <copyright file="KeymapRules.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Protocol;

namespace PencilBox.Core.Models;

/// <summary>
/// Keymap 编辑规则（pc-issues/04 §2A，2026-10-08 裁决）：
/// K2(0x02) 的事件列是笔↔Dongle 的**语音会话边界协议契约**——笔端把两种启动方式
/// （0x21 可切的单击翻转/长按 PTT）归一为 [2][0x03] 开始 / [2][0x04] 结束 上报，
/// 事件列与"启动方式"无关，用户不可改；动作列（F13/F14 → 任意键值）可个性化。
/// </summary>
public static class KeymapRules
{
    /// <summary>K2 的 key_id</summary>
    public const byte K2KeyId = 0x02;

    /// <summary>锁定原因（CLI/UI 共用文案）</summary>
    public const string K2EventLockReason =
        "K2 事件列是语音会话边界协议契约（会话开始/结束，笔端硬编码归一上报，与启动方式无关），" +
        "修改将断语音启停；个性化请改动作/键值/修饰键（pc-issues/04 §2A）";

    /// <summary>是否 K2 行</summary>
    public static bool IsK2(byte keyId) => keyId == K2KeyId;

    /// <summary>K2 行允许的事件（仅会话开始/结束边界）</summary>
    public static bool IsK2EventAllowed(KeyEventType eventType) =>
        eventType is KeyEventType.LongPress or KeyEventType.LongRelease;

    /// <summary>校验：K2 行事件被改即违规（CLI 拒写 / UI 锁格共用）</summary>
    public static bool IsK2EventViolation(byte keyId, KeyEventType eventType) =>
        IsK2(keyId) && !IsK2EventAllowed(eventType);
}
