// <copyright file="SrxKeyActionRouterTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Ble.SoftReceiver;
using PencilBox.Infrastructure.Platform;

namespace PencilBox.Tests;

/// <summary>
/// 语义键动作映射单测：0x13 [key_id][action] → PC 抽象动作
/// （口径=笔端 app_key_action.c CONFIG_DONGLE_MODE=1 实发表；默认表对齐硬件接收器语义）。
/// </summary>
public class SrxKeyActionRouterTests
{
    [Theory]
    [InlineData((byte)5, SrxActionKind.VolumeUp)]
    [InlineData((byte)6, SrxActionKind.VolumeDown)]
    [InlineData((byte)3, SrxActionKind.PageUp)]
    [InlineData((byte)4, SrxActionKind.PageDown)]
    public void Map_Click_MapsToHardwareEquivalent(byte keyNum, SrxActionKind expected)
    {
        var plan = SrxKeyActionRouter.Map(keyNum, SrxKeyActionRouter.ActClick);
        Assert.Equal(expected, plan.Kind);
        Assert.Equal(1, plan.Presses);
    }

    [Fact]
    public void Map_DoubleClick_PressesTwice()
    {
        var plan = SrxKeyActionRouter.Map(5, SrxKeyActionRouter.ActDoubleClick);
        Assert.Equal(SrxActionKind.VolumeUp, plan.Kind);
        Assert.Equal(2, plan.Presses);
    }

    [Fact]
    public void Map_LongPress_IsSingleTapPerEvent_ContinuousVolumeViaPenRepeat()
    {
        // 笔端长按按 REPEAT_MS 节流连发 0x03，PC 每条执行一次即连续调量
        var plan = SrxKeyActionRouter.Map(6, SrxKeyActionRouter.ActLongPress);
        Assert.Equal(SrxActionKind.VolumeDown, plan.Kind);
        Assert.Equal(1, plan.Presses);
    }

    [Fact]
    public void Map_LongRelease_IsEndSentinel_NoInjection()
    {
        var plan = SrxKeyActionRouter.Map(5, SrxKeyActionRouter.ActLongRelease);
        Assert.Equal(SrxActionKind.VolumeUp, plan.Kind);
        Assert.Equal(0, plan.Presses);
    }

    [Fact]
    public void Map_VoiceKey_IsBoundaryOnly()
    {
        var plan = SrxKeyActionRouter.Map(2, SrxKeyActionRouter.ActLongPress);
        Assert.Equal(SrxActionKind.VoiceBoundary, plan.Kind);
        Assert.Equal(0, plan.Presses);
    }

    [Fact]
    public void Map_AirmouseKey_IsLocalControl_NoAirAction()
    {
        var plan = SrxKeyActionRouter.Map(1, SrxKeyActionRouter.ActClick);
        Assert.Equal(SrxActionKind.None, plan.Kind);
        Assert.Equal(0, plan.Presses);
    }

    [Fact]
    public void Map_UnknownKeyNum_Ignored()
    {
        var plan = SrxKeyActionRouter.Map(9, SrxKeyActionRouter.ActClick);
        Assert.Equal(SrxActionKind.None, plan.Kind);
        Assert.Equal(0, plan.Presses);
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
    public void VkOf_CoversInjectableKindsOnly()
    {
        // SystemInputInjector 的纯静态映射（P/Invoke 实例方法不在单测面）
        Assert.Equal(0xAF, SystemInputInjector.VkOf(SrxActionKind.VolumeUp));
        Assert.Equal(0xAE, SystemInputInjector.VkOf(SrxActionKind.VolumeDown));
        Assert.Equal(0x21, SystemInputInjector.VkOf(SrxActionKind.PageUp));
        Assert.Equal(0x22, SystemInputInjector.VkOf(SrxActionKind.PageDown));
        Assert.Null(SystemInputInjector.VkOf(SrxActionKind.VoiceBoundary));
        Assert.Null(SystemInputInjector.VkOf(SrxActionKind.None));
    }
}
