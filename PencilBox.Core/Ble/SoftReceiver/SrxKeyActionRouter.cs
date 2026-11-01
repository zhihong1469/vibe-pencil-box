// <copyright file="SrxKeyActionRouter.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Ble.SoftReceiver;

/// <summary>语义键映射出的 PC 侧抽象动作（注入执行在 Infrastructure/CLI，Core 只管映射）。</summary>
public enum SrxActionKind
{
    /// <summary>不执行（K1 飞鼠开关零空口 / 松手哨兵 / 未知键）。</summary>
    None = 0,

    /// <summary>音量加（K5，Win32 VK_VOLUME_UP）。</summary>
    VolumeUp,

    /// <summary>音量减（K6，Win32 VK_VOLUME_DOWN）。</summary>
    VolumeDown,

    /// <summary>前翻（K3 ▲，Win32 VK_PRIOR）。</summary>
    PageUp,

    /// <summary>后翻（K4 ▼，Win32 VK_NEXT）。</summary>
    PageDown,

    /// <summary>K2 语音键边界（[2][0x03]）——默认只记录；识别链接入后此处即识别触发缝。</summary>
    VoiceBoundary,
}

/// <summary>一次语义键动作的执行计划（Presses=注入次数；0=只记录不注入）。</summary>
public sealed record SrxActionPlan(SrxActionKind Kind, int Presses, string Note)
{
    public static SrxActionPlan Ignored(string why) => new(SrxActionKind.None, 0, why);
}

/// <summary>
/// 笔端语义键（0x13 [key_id][action]）→ PC 抽象动作映射（纯函数，可单测）。
/// 笔端实发口径（ble_app_rc app_key_action.c，CONFIG_DONGLE_MODE=1 实锤）：
/// K1 飞鼠开关本地翻转零空口；K2 语音键边界 [2][0x03]（会话开始，方式结束另有哨兵）；
/// K3/K4=前翻▲/后翻▼、K5/K6=音量加/减，动作=单击 0x01 / 双击 0x02 / 长按 0x03
/// （笔端按 CGC_KEY_*_REPEAT_MS 节流连发，PC 每条执行一次即天然连续调量/翻页）/
/// 松手 0x04（结束哨兵，不注入）；空鼠开时 K3/K4 改走鼠标槽（bit0=左键 bit1=右键），
/// 不再发 0x13。默认表与硬件接收器语义对齐（换软件接收器按键行为不变，低惊讶）。
/// </summary>
public static class SrxKeyActionRouter
{
    public const byte ActClick = 0x01;
    public const byte ActDoubleClick = 0x02;
    public const byte ActLongPress = 0x03;
    public const byte ActLongRelease = 0x04;

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
        ActClick => "单击",
        ActDoubleClick => "双击",
        ActLongPress => "长按连发",
        ActLongRelease => "松手",
        _ => $"act=0x{act:X2}",
    };

    /// <summary>映射一条语义键事件为执行计划。</summary>
    public static SrxActionPlan Map(byte keyNum, byte act)
    {
        var kind = keyNum switch
        {
            3 => SrxActionKind.PageUp,
            4 => SrxActionKind.PageDown,
            5 => SrxActionKind.VolumeUp,
            6 => SrxActionKind.VolumeDown,
            2 => SrxActionKind.VoiceBoundary,
            _ => SrxActionKind.None,
        };

        // 长按连发：笔端已按节流间隔连发 0x03，PC 每条执行一次即连续；
        // 松手 0x04 是结束哨兵（连续动作终止），不注入。
        var presses = act switch
        {
            ActClick => 1,
            ActDoubleClick => 2,
            ActLongPress => 1,
            _ => 0,
        };
        if (kind is SrxActionKind.None or SrxActionKind.VoiceBoundary)
            presses = 0;

        return kind switch
        {
            SrxActionKind.None => SrxActionPlan.Ignored(
                keyNum == 1 ? "K1 飞鼠开关=笔端本地控制，无空口动作" : "未知键不执行"),
            SrxActionKind.VoiceBoundary => new SrxActionPlan(kind, 0,
                "语音键边界（识别触发缝，识别链接入前只记录）"),
            _ => new SrxActionPlan(kind, presses, $"{kind}×{presses}"),
        };
    }
}
