// <copyright file="SrxKeyActionRouter.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Models;
using PencilBox.Core.Protocol;

namespace PencilBox.Core.Ble.SoftReceiver;

/// <summary>
/// 笔端语义键（0x13）与 S1 档位（0x16）→ 快捷键映射路由（软件键鼠的键盘腿）。
/// 查表对象 = <see cref="KeymapEntry"/> 按键表 + <see cref="KeymapSwitchEntry"/> 切换表
/// （与硬件 Dongle `srv_keymap` 同源的两张表；默认 = 出厂真值 <see cref="KeymapFactoryDefault"/>，
/// 固件表变→黄金测试红灯→本表同步）。事件码直接对齐：VP_KEY_ACT 0x01~0x04 =
/// <see cref="KeyEventType"/> Click/DoubleClick/LongPress/LongRelease。
/// 鼠标腿不走本表——空鼠槽 [buttons][x][y][wheel] 是原始相对量，注入侧直通。
/// </summary>
public sealed class SrxKeyActionRouter
{
    private readonly Dictionary<(byte KeyId, KeyEventType Evt), KeymapEntry> _entries;
    private readonly KeymapSwitchEntry[] _switchEntries;

    public SrxKeyActionRouter(IEnumerable<KeymapEntry> entries, IEnumerable<KeymapSwitchEntry>? switchEntries = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = entries.ToDictionary(e => (e.KeyId, e.EventType));
        _switchEntries = switchEntries?.ToArray() ?? [];
    }

    /// <summary>出厂默认表（固件 `s_keymap_default` + `s_switch_default` 镜像）。</summary>
    public static SrxKeyActionRouter FromFactory() =>
        new(KeymapFactoryDefault.Entries, KeymapFactoryDefault.SwitchEntries);

    /// <summary>
    /// 0x13 [key_id][action] 查表。无配置/未知动作码返回 null（调用方只记录不注入）。
    /// 笔端口径：K1 飞鼠开关零空口不落表；K2 会话边界 [2][0x03/0x04]→F13/F14；
    /// K3-K6 单击/长按连发查表注入（长按连发节奏=笔端宏，PC 每条执行一次即连续）；
    /// 双击 0x02 查 DOUBLE 条目（出厂表无 DOUBLE 行=双击不动作，与硬件一致）。
    /// </summary>
    public KeymapEntry? Map(byte keyId, byte act)
    {
        var evt = (KeyEventType)act;
        if (!Enum.IsDefined(evt) || evt is KeyEventType.LongSpecial)
            return null;
        return _entries.TryGetValue((keyId, evt), out var e) ? e : null;
    }

    /// <summary>S1 档位（0x16 mode）查切换表；无配置返回 null。</summary>
    public KeymapSwitchEntry? MapSwitch(byte mode) =>
        _switchEntries.FirstOrDefault(s => s.Mode == mode);

    /// <summary>键号语义（笔端 CGC_KEY 枚举）：1=飞鼠开关 2=语音 3=▲前翻 4=▼后翻 5=VOL+ 6=VOL−。</summary>
    public static string KeyName(byte keyNum) => keyNum switch
    {
        1 => "K1 飞鼠开关",
        2 => "K2 语音",
        3 => "K3 前翻▲",
        4 => "K4 后翻▼",
        5 => "K5 VOL+",
        6 => "K6 VOL−",
        _ => $"Key{keyNum}",
    };

    /// <summary>动作码语义（VP_KEY_ACT_*）。</summary>
    public static string ActionName(byte act) => act switch
    {
        0x01 => "单击",
        0x02 => "双击",
        0x03 => "长按连发",
        0x04 => "松手",
        _ => $"act=0x{act:X2}",
    };

    /// <summary>表条目的人类描述（注入日志用）。</summary>
    public static string DescribeEntry(KeymapEntry e) => e.ActionType switch
    {
        KeyActionType.Keyboard => $"键盘 0x{e.CodeLo:X2}{(e.Modifier != 0 ? $"+mod0x{e.Modifier:X2}" : "")}",
        KeyActionType.Consumer => $"消费键 0x{((e.CodeHi << 8) | e.CodeLo):X3}",
        KeyActionType.MouseButton => "鼠标键",
        KeyActionType.None => "无动作",
        _ => $"{e.ActionType} 0x{e.CodeLo:X2}",
    };
}
