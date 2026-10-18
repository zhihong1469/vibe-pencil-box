// <copyright file="PenWorkStateTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Models;
using PencilBox.Core.Protocol;
using PencilBox.Core.Protocol.Company;

namespace PencilBox.Tests;

/// <summary>
/// 笔端工作状态自管（产品落地/byte2…/PC 落地回执 §三，2026-10-18）：
/// 绑定推导（Keymap 联动）、两轴状态机转移、VK→HID usage 换算、`0x55` 帧面黄金字节（pc-issues/05 §2 草案）。
/// </summary>
public class PenWorkStateTests
{
    // ================= 绑定推导（Keymap 联动） =================

    [Fact]
    public void FromKeymap_FactoryDefault_YieldsF13F14F15F16()
    {
        var b = PenWorkStateBinding.FromKeymap(
            KeymapFactoryDefault.Entries, KeymapFactoryDefault.SwitchEntries, "出厂默认");

        Assert.Equal((byte)0x68, b.SessionStartUsage);   // K2 长按 → F13（0x03 会话开始）
        Assert.Equal((byte)0x69, b.SessionEndUsage);     // K2 长放 → F14（0x04 会话结束）
        Assert.Equal((byte)0x6A, b.AmpOnUsage);          // S1=SPK → F15
        Assert.Equal((byte)0x6B, b.AmpOffUsage);         // S1=ON  → F16
        Assert.True(b.IsUsable);
        Assert.Equal("出厂默认", b.SourceName);
    }

    [Fact]
    public void FromKeymap_CustomRemap_FollowsConfig()
    {
        var entries = new List<KeymapEntry>
        {
            new() { KeyId = 0x02, EventType = KeyEventType.LongPress, ActionType = KeyActionType.Keyboard, CodeLo = 0x04 },
            new() { KeyId = 0x02, EventType = KeyEventType.LongRelease, ActionType = KeyActionType.Keyboard, CodeLo = 0x05 },
        };
        var switches = new List<KeymapSwitchEntry>
        {
            new() { Mode = 0x02, Modifier = 0x00, CodeLo = 0x0A },
            new() { Mode = 0x01, Modifier = 0x00, CodeLo = 0x0B },
        };

        var b = PenWorkStateBinding.FromKeymap(entries, switches, "自定义");

        Assert.Equal((byte)0x04, b.SessionStartUsage);
        Assert.Equal((byte)0x05, b.SessionEndUsage);
        Assert.Equal((byte)0x0A, b.AmpOnUsage);
        Assert.Equal((byte)0x0B, b.AmpOffUsage);
    }

    [Fact]
    public void FromKeymap_NonKeyboardOrModifierRows_Ignored()
    {
        var entries = new List<KeymapEntry>
        {
            // K2 长按映射到消费键动作：键盘钩子观测不到，不参与推导
            new() { KeyId = 0x02, EventType = KeyEventType.LongPress, ActionType = KeyActionType.Consumer, CodeLo = 0xE9 },
            // 非 K2 键的键盘动作行：与会话边界无关
            new() { KeyId = 0x03, EventType = KeyEventType.LongPress, ActionType = KeyActionType.Keyboard, CodeLo = 0x04 },
        };
        var switches = new List<KeymapSwitchEntry>
        {
            // 带修饰键的组合不参与（钩子只匹配裸主键）
            new() { Mode = 0x02, Modifier = 0x01, CodeLo = 0x0A },
        };

        var b = PenWorkStateBinding.FromKeymap(entries, switches, "x");

        Assert.Equal((byte)0, b.SessionStartUsage);
        Assert.Equal((byte)0, b.AmpOnUsage);
        Assert.False(b.IsUsable);
    }

    // ================= 状态机 =================

    private static PenWorkStateTracker NewFactoryTracker() => new(
        PenWorkStateBinding.FromKeymap(
            KeymapFactoryDefault.Entries, KeymapFactoryDefault.SwitchEntries, "出厂默认"));

    [Fact]
    public void Tracker_StartsUnknown()
    {
        var t = NewFactoryTracker();
        Assert.False(t.IsKnown);
        Assert.Null(t.SessionActive);
        Assert.Null(t.AmpActive);
        Assert.Equal("待事件", t.DisplayText);
    }

    [Fact]
    public void Tracker_SessionAndAmpTransitions()
    {
        var t = NewFactoryTracker();

        Assert.True(t.Feed(0x68));                        // F13 会话开始
        Assert.True(t.SessionActive);
        Assert.Null(t.AmpActive);
        Assert.Equal("识别中", t.DisplayText);

        Assert.True(t.Feed(0x6A));                        // F15 扩音开
        Assert.True(t.AmpActive);
        Assert.Equal("识别中 · 扩音档", t.DisplayText);

        Assert.True(t.Feed(0x6B));                        // F16 扩音关
        Assert.False(t.AmpActive);
        Assert.True(t.SessionActive);
        Assert.Equal("识别中", t.DisplayText);

        Assert.True(t.Feed(0x69));                        // F14 会话结束
        Assert.False(t.SessionActive);
        Assert.False(t.AmpActive);
        Assert.Equal("常态", t.DisplayText);
    }

    [Fact]
    public void Tracker_RepeatedOrUnknownKeys_NoChange()
    {
        var t = NewFactoryTracker();

        Assert.True(t.Feed(0x68));
        Assert.False(t.Feed(0x68));                       // 重复"开始"不重报
        Assert.False(t.Feed(0x2C));                       // 空格等无关键
        Assert.False(t.Feed(0));                          // usage=0 防御（未映射键）
        Assert.True(t.SessionActive);
        Assert.Null(t.AmpActive);
    }

    [Fact]
    public void Tracker_Reset_BackToUnknown()
    {
        var t = NewFactoryTracker();
        t.Feed(0x68);
        t.Feed(0x6A);

        t.Reset();

        Assert.False(t.IsKnown);
        Assert.Null(t.SessionActive);
        Assert.Null(t.AmpActive);
        Assert.Equal("待事件", t.DisplayText);
    }

    // ================= VK → HID usage =================

    [Theory]
    [InlineData(0x7C, 0x68)]   // F13（出厂识别开始）
    [InlineData(0x7D, 0x69)]   // F14
    [InlineData(0x87, 0x73)]   // F24（F 区尾）
    [InlineData(0x70, 0x3A)]   // F1
    [InlineData(0x7B, 0x45)]   // F12
    [InlineData(0x41, 0x04)]   // 'A'
    [InlineData(0x5A, 0x1D)]   // 'Z'
    [InlineData(0x31, 0x1E)]   // '1'
    [InlineData(0x30, 0x27)]   // '0'（数字区特例：排尾）
    [InlineData(0x20, 0x2C)]   // Space
    [InlineData(0x60, 0x00)]   // 小键盘 0 未覆盖 → 0（不可联动）
    public void FromVirtualKey_MapsExpected(int vk, byte expected)
        => Assert.Equal(expected, KeyboardUsageMap.FromVirtualKey(vk));

    // ================= 0x55 帧面（黄金字节） =================

    [Fact]
    public void MetaProbe_GoldenBytes()
    {
        // F5 AA AA [LEN=5] 61 01 55 01 FF（PARAM 01 + slot=0xFF 元信息探测）
        Assert.Equal(new byte[] { 0xF5, 0xAA, 0xAA, 0x05, 0x61, 0x01, 0x55, 0x01, 0xFF },
            KeymapConfigFrames.EncodeMetaProbe());
    }

    [Fact]
    public void Commit_GoldenBytes()
    {
        Assert.Equal(new byte[] { 0xF5, 0xAA, 0xAA, 0x04, 0x61, 0x01, 0x55, 0x03 },
            KeymapConfigFrames.EncodeCommit());
    }

    [Fact]
    public void WriteEntry_GoldenLayout_Len0x0D()
    {
        var entry = new KeymapEntry
        {
            KeyId = 0x02, EventType = KeyEventType.LongPress,
            ActionType = KeyActionType.Keyboard, CodeLo = 0x68
        };

        var frame = KeymapConfigFrames.EncodeWriteEntry(3, entry);

        Assert.Equal(0x0D, frame[3]);                     // LEN = 3 + [02][slot][entry8] 10B
        Assert.Equal(0x55, frame[6]);                     // CMDTYPE
        Assert.Equal(0x02, frame[7]);                     // PARAM=写条目
        Assert.Equal(0x03, frame[8]);                     // slot=固件物理槽
        Assert.Equal(entry.ToBytes(), frame[9..17]);
    }

    [Fact]
    public void WriteSwitch_GoldenBytes_AndInternalRowRejected()
    {
        Assert.Equal(new byte[] { 0xF5, 0xAA, 0xAA, 0x07, 0x61, 0x01, 0x55, 0x06, 0x02, 0x00, 0x6A },
            KeymapConfigFrames.EncodeWriteSwitch(new KeymapSwitchEntry { Mode = 0x02, Modifier = 0x00, CodeLo = 0x6A }));

        // mode=0 基站内部行：固件应拒写，PC 主动不发
        Assert.Throws<ArgumentException>(() =>
            KeymapConfigFrames.EncodeWriteSwitch(new KeymapSwitchEntry { Mode = 0x00, Modifier = 0x00, CodeLo = 0x6B }));
    }

    [Fact]
    public void TryParseMeta_ParsesStatusCountMaxVersion()
    {
        // 6B 精确长：[PARAM=01][STATUS][count][max][ver2B]
        Assert.True(KeymapConfigFrames.TryParseMeta(
            [0x01, 0x00, 0x03, 0x10, 0x00, 0x01],
            out var status, out var count, out var max, out var version));

        Assert.Equal(0x00, status);
        Assert.Equal(3, count);
        Assert.Equal(16, max);
        Assert.Equal(1, version);

        // PARAM 不对 / 长度不足 → false
        Assert.False(KeymapConfigFrames.TryParseMeta([0x02, 0x00, 0x03, 0x10, 0x00, 0x01], out _, out _, out _, out _));
        Assert.False(KeymapConfigFrames.TryParseMeta([0x01, 0x00, 0x03, 0x10, 0x00], out _, out _, out _, out _));
    }

    [Fact]
    public void TryParseResponse_NeedsParamAndStatus()
    {
        Assert.True(KeymapConfigFrames.TryParseResponse([0x02, 0x00], out var param, out var status));
        Assert.Equal(0x02, param);
        Assert.Equal(0x00, status);

        Assert.False(KeymapConfigFrames.TryParseResponse([0x02], out _, out _));
    }
}
