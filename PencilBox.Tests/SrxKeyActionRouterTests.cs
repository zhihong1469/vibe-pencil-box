// <copyright file="SrxKeyActionRouterTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Ble.SoftReceiver;
using PencilBox.Core.Models;
using PencilBox.Core.Protocol;
using PencilBox.Infrastructure.Platform;

namespace PencilBox.Tests;

/// <summary>
/// Keymap 表驱动键路由单测：0x13/S1 → 出厂表条目（口径=固件 srv_keymap.c 出厂真值镜像）；
/// 注入侧纯映射（usage→VK、Consumer→VK）对拍。
/// </summary>
public class SrxKeyActionRouterTests
{
    private readonly SrxKeyActionRouter _router = SrxKeyActionRouter.FromFactory();

    [Fact]
    public void FactoryTable_K3K4Click_MapToPageKeys()
    {
        var k3 = _router.Map(3, 0x01);
        Assert.NotNull(k3);
        Assert.Equal(KeyActionType.Keyboard, k3.ActionType);
        Assert.Equal(0x4B, k3.CodeLo);          // PageUp usage

        var k4 = _router.Map(4, 0x01);
        Assert.NotNull(k4);
        Assert.Equal(0x4E, k4.CodeLo);          // PageDown usage
    }

    [Fact]
    public void FactoryTable_K5K6_MapToConsumerVolume()
    {
        var k5 = _router.Map(5, 0x01);
        Assert.NotNull(k5);
        Assert.Equal(KeyActionType.Consumer, k5.ActionType);
        Assert.Equal(0xE9, k5.CodeLo);          // Volume Increment

        var k6 = _router.Map(6, 0x03);          // 长按连发条目
        Assert.NotNull(k6);
        Assert.Equal(0xEA, k6.CodeLo);          // Volume Decrement
    }

    [Fact]
    public void FactoryTable_K2SessionBoundary_F13F14()
    {
        var start = _router.Map(2, 0x03);       // 会话开始 [2][0x03]
        Assert.NotNull(start);
        Assert.Equal(0x68, start.CodeLo);       // F13

        var end = _router.Map(2, 0x04);         // 会话结束 [2][0x04]
        Assert.NotNull(end);
        Assert.Equal(0x69, end.CodeLo);         // F14
    }

    [Fact]
    public void Map_DoubleClick_NoFactoryRow_Null()
    {
        // 出厂表无 DOUBLE 行=双击不动作（与硬件一致）；有独立配置行时可查到
        Assert.Null(_router.Map(5, 0x02));
    }

    [Fact]
    public void Map_K1AndUnknown_NoInjection()
    {
        Assert.Null(_router.Map(1, 0x01));      // K1 飞鼠开关零空口不落表
        Assert.Null(_router.Map(9, 0x01));      // 未知键
        Assert.Null(_router.Map(5, 0x05));      // LongSpecial 不映射
        Assert.Null(_router.Map(5, 0xFF));      // 未知动作码
    }

    [Fact]
    public void MapSwitch_FactoryS1_F15F16()
    {
        Assert.Equal((byte)0x6A, _router.MapSwitch(0x02)?.CodeLo);   // 扩音 → F15
        Assert.Equal((byte)0x6B, _router.MapSwitch(0x01)?.CodeLo);   // 识别 → F16
        Assert.Equal((byte)0x6B, _router.MapSwitch(0x00)?.CodeLo);   // 待机 → F16
        Assert.Null(_router.MapSwitch(0x03));
    }

    [Fact]
    public void CustomTable_OverridesFactory()
    {
        var custom = new SrxKeyActionRouter(
        [
            new() { KeyId = 0x03, EventType = KeyEventType.Click, ActionType = KeyActionType.Keyboard, CodeLo = 0x04 }
        ]);
        var entry = custom.Map(3, 0x01);
        Assert.Equal((byte)0x04, entry?.CodeLo);
        Assert.Null(custom.Map(5, 0x01));       // 自定义表没配的键不注入
    }

    [Fact]
    public void Names_CoverFactoryKeysAndActions()
    {
        Assert.Equal("K5 VOL+", SrxKeyActionRouter.KeyName(5));
        Assert.Equal("K2 语音", SrxKeyActionRouter.KeyName(2));
        Assert.Equal("单击", SrxKeyActionRouter.ActionName(0x01));
        Assert.Equal("松手", SrxKeyActionRouter.ActionName(0x04));
    }

    [Fact]
    public void UsageToVirtualKey_CoversKeymapUsages()
    {
        Assert.Equal(0x21, SyntheticKeyboardSender.UsageToVirtualKey(0x4B));   // PageUp
        Assert.Equal(0x22, SyntheticKeyboardSender.UsageToVirtualKey(0x4E));   // PageDown
        Assert.Equal(0x7C, SyntheticKeyboardSender.UsageToVirtualKey(0x68));   // F13
        Assert.Equal(0x7D, SyntheticKeyboardSender.UsageToVirtualKey(0x69));   // F14
        Assert.Equal(0x7E, SyntheticKeyboardSender.UsageToVirtualKey(0x6A));   // F15
        Assert.Equal(0x7F, SyntheticKeyboardSender.UsageToVirtualKey(0x6B));   // F16
    }

    [Fact]
    public void ConsumerVk_VolumeFamilyOnly()
    {
        Assert.Equal(0xAF, SystemInputInjector.ConsumerVk(0xE9, 0x00));        // VOL+
        Assert.Equal(0xAE, SystemInputInjector.ConsumerVk(0xEA, 0x00));        // VOL−
        Assert.Null(SystemInputInjector.ConsumerVk(0xB5, 0x00));               // Scan Next Track=未支持
    }
}
