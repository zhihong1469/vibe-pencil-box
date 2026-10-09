// <copyright file="KeymapFactoryDefault.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Protocol;

namespace PencilBox.Core.Models;

/// <summary>
/// 出厂默认 Keymap——字节级照抄固件
/// `jl_dongle/apps/spp_and_le/examples/dongle/srv/keymap/srv_keymap.c` 的
/// `s_keymap_default`（10 条，2026-09-22 版；pc-issues/04 提案 A）。
///
/// 固件表实有 11 条：末行 {0xF0,0x05} 组合键清配对为"预留"行，笔端无发送方
/// （全仓零命中；K3+K4 组合已退役，现存组合仅为调试专用本地清绑定，不走空口），
/// 不属于产品正常运行动作——经用户裁决（2026-10-08）不进 PC 显示与配置。
/// 固件表变更时仍需同步本表——单一来源登记点 = pc-issues/04 §2。
///
/// 控制权边界（照抄固件注释）：K1 飞鼠翻转、K2 语音启停由笔端本地控制（不落本表）；
/// K3/K4 双语义（飞鼠开=鼠标键）亦由笔端本地完成——本表只承载产生 USB HID 报告的
/// 翻页/音量键；长按连发节奏由笔端宏决定（翻页 300ms / 音量 400ms）。
/// </summary>
public static class KeymapFactoryDefault
{
    /// <summary>出厂默认条目（索引即接收器表槽位）</summary>
    public static KeymapEntry[] Entries { get; } =
    [
        new() { KeyId = 0x03, EventType = KeyEventType.Click, ActionType = KeyActionType.Keyboard, CodeLo = 0x4B },      // K3 前翻▲ → PageUp
        new() { KeyId = 0x04, EventType = KeyEventType.Click, ActionType = KeyActionType.Keyboard, CodeLo = 0x4E },      // K4 后翻▼ → PageDown
        new() { KeyId = 0x05, EventType = KeyEventType.Click, ActionType = KeyActionType.Consumer, CodeLo = 0xE9 },      // K5 → VOL+
        new() { KeyId = 0x06, EventType = KeyEventType.Click, ActionType = KeyActionType.Consumer, CodeLo = 0xEA },      // K6 → VOL−
        new() { KeyId = 0x03, EventType = KeyEventType.LongPress, ActionType = KeyActionType.Keyboard, CodeLo = 0x4B },  // K3 长按 → PageUp（笔端 300ms 连发）
        new() { KeyId = 0x04, EventType = KeyEventType.LongPress, ActionType = KeyActionType.Keyboard, CodeLo = 0x4E },  // K4 长按 → PageDown（笔端 300ms 连发）
        new() { KeyId = 0x05, EventType = KeyEventType.LongPress, ActionType = KeyActionType.Consumer, CodeLo = 0xE9 },  // K5 长按 → VOL+（笔端 400ms 连发）
        new() { KeyId = 0x06, EventType = KeyEventType.LongPress, ActionType = KeyActionType.Consumer, CodeLo = 0xEA },  // K6 长按 → VOL−（笔端 400ms 连发）
        new() { KeyId = 0x02, EventType = KeyEventType.LongPress, ActionType = KeyActionType.Keyboard, Modifier = 0x04, CodeLo = 0x42 },   // K2 语音会话开始 → ALT+F9（笔端两种启动方式均归一为此边界上报；2026-10-08 键值变更 ALT+1→ALT+F9）
        new() { KeyId = 0x02, EventType = KeyEventType.LongRelease, ActionType = KeyActionType.Keyboard, Modifier = 0x04, CodeLo = 0x43 }, // K2 语音会话结束 → ALT+F10（2026-10-08 键值变更 ALT+2→ALT+F10）
    ];

    /// <summary>
    /// S1 档位切换快捷键（字节级照抄固件 `srv_keymap.c s_switch_default`，
    /// 2026-10-08 版：SPK→ALT+F11 / ON·IDLE→ALT+F12；同日由 ALT+3/4 变更）。
    /// 切换表与按键表是两张表：本表暂无读写协议，PC 只做出厂真值只读显示（pc-issues/04 §4）。
    /// </summary>
    public static KeymapSwitchEntry[] SwitchEntries { get; } =
    [
        new() { Mode = 0x02, Modifier = 0x04, CodeLo = 0x44 },  // 扩音(SPK) → ALT+F11
        new() { Mode = 0x01, Modifier = 0x04, CodeLo = 0x45 },  // 识别(ON)  → ALT+F12（回非扩音也必须上传，否则 PC 状态回不去）
        new() { Mode = 0x00, Modifier = 0x04, CodeLo = 0x45 },  // 待机(IDLE) → ALT+F12
    ];
}
