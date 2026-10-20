// <copyright file="ChargeTimingTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Models;
using PencilBox.Core.Protocol.Company;

namespace PencilBox.Tests;

/// <summary>
/// 充电/放电计时器（指挥官 2026-10-20 需求）——合成事件序列全场景：
/// 完整周期双口径、中止、满电态接续、幂等、未知电量、断连走表、放电定格与新周期。
/// </summary>
public class ChargeTimingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(double minutes) => T0.AddMinutes(minutes);

    private const byte None = PenChargeState.None;
    private const byte Charging = PenChargeState.Charging;
    private const byte Full = PenChargeState.Full;

    [Fact]
    public void FullCycle_TwoClocks_DischargeFrozenByNextCharge()
    {
        var t = new ChargeTimingTracker();
        t.Feed(At(0), None, 3);                       // 0% 附近在电外
        t.Feed(At(10), Charging, 5);                  // 插充（自 5% 起）
        t.Feed(At(70), Charging, 100);                // 显示到 100% → 显示口径 60 分
        t.Feed(At(100), Full, 100);                   // PMU FINISH → PMU 口径 90 分
        t.Feed(At(120), None, 98);                    // 拔出 → 放电起表

        var r1 = t.Report(At(150));
        Assert.False(r1.Charging);
        Assert.Equal(TimeSpan.FromMinutes(60), r1.ToDisplayFull);
        Assert.Equal(TimeSpan.FromMinutes(90), r1.ToPmuFull);
        Assert.False(r1.ChargeAborted);
        Assert.Equal(TimeSpan.FromMinutes(30), r1.DischargeElapsed);   // 墙钟走表
        Assert.True(r1.DischargeTicking);
        Assert.Equal("自充满取下", r1.DischargeOrigin);
        Assert.Equal((byte?)5, r1.StartPercent);

        t.Feed(At(180), Charging, 40);                // 再插充 → 放电定格 + 新周期
        var r2 = t.Report(At(200));
        Assert.Equal(TimeSpan.FromMinutes(60), r2.DischargeElapsed);   // 定格不随墙钟走
        Assert.False(r2.DischargeTicking);
        Assert.True(r2.Charging);
        Assert.Equal(TimeSpan.FromMinutes(20), r2.ChargeElapsed);
        Assert.Null(r2.ToDisplayFull);                // 新周期口径清零重计
        Assert.Null(r2.ToPmuFull);
        Assert.Equal((byte?)40, r2.StartPercent);
    }

    [Fact]
    public void AbortedCharge_ElapsedUntilUnplug_DischargeStillStarts()
    {
        var t = new ChargeTimingTracker();
        t.Feed(At(0), None, 50);
        t.Feed(At(10), Charging, 60);
        t.Feed(At(40), None, 70);                     // 未充满拔出

        var r = t.Report(At(55));
        Assert.True(r.ChargeAborted);
        Assert.Equal(TimeSpan.FromMinutes(30), r.ChargeElapsed);
        Assert.Null(r.ToDisplayFull);
        Assert.Null(r.ToPmuFull);
        Assert.Equal("未充满拔出", r.DischargeOrigin);
        Assert.Equal(TimeSpan.FromMinutes(15), r.DischargeElapsed);
    }

    [Fact]
    public void FullWithoutObservedStart_DurationsUnknown_DischargeOriginFull()
    {
        var t = new ChargeTimingTracker();
        t.Feed(At(0), Full, 100);                     // 满电态重连：起点未观测

        var r1 = t.Report(At(10));
        Assert.True(r1.Charging);
        Assert.Null(r1.ToDisplayFull);
        Assert.Null(r1.ToPmuFull);
        Assert.Null(r1.ChargeElapsed);                // 无起点不计"计中"

        t.Feed(At(30), None, 99);
        var r2 = t.Report(At(40));
        Assert.Equal("自充满取下", r2.DischargeOrigin);
        Assert.False(r2.ChargeAborted);
        Assert.Equal(TimeSpan.FromMinutes(10), r2.DischargeElapsed);
    }

    [Fact]
    public void RepeatedFeeds_Idempotent_First100PercentMarkKept()
    {
        var t = new ChargeTimingTracker();
        t.Feed(At(0), None, 10);
        t.Feed(At(5), Charging, 15);
        t.Feed(At(65), Charging, 100);
        t.Feed(At(95), Charging, 100);                // 重复 100%：首达定格不改写
        t.Feed(At(96), Full, 100);
        t.Feed(At(97), Full, 100);                    // 重复充满：不改写

        var r = t.Report(At(100));
        Assert.Equal(TimeSpan.FromMinutes(60), r.ToDisplayFull);
        Assert.Equal(TimeSpan.FromMinutes(91), r.ToPmuFull);
    }

    [Fact]
    public void UnknownPercent_TransitionsStillWork_DisplayClockWaits()
    {
        var t = new ChargeTimingTracker();
        t.Feed(At(0), None, null);
        t.Feed(At(5), Charging, null);                // 0x53 无电量槽 / 0xFF
        t.Feed(At(125), Full, null);                  // 直接到 PMU 充满

        var r = t.Report(At(130));
        Assert.Null(r.ToDisplayFull);                 // 显示口径等不到 100% 观测
        Assert.Equal(TimeSpan.FromMinutes(120), r.ToPmuFull);
        Assert.Null(r.StartPercent);
    }

    [Fact]
    public void RepeatedNotCharging_DoesNotRestartDischarge()
    {
        var t = new ChargeTimingTracker();
        t.Feed(At(0), Charging, 20);
        t.Feed(At(60), None, 30);                     // 拔出 → 放电起表
        t.Feed(At(90), None, 20);                     // 重复非充电：不重置放电表
        t.Feed(At(120), None, 10);

        var r = t.Report(At(130));
        Assert.Equal(TimeSpan.FromMinutes(70), r.DischargeElapsed);   // 自 60 分起算
        Assert.True(t.IsTicking);                                     // 放电走表中
    }

    [Fact]
    public void NoCycle_ShowsPlaceholder_NotConfusedWithRealData()
    {
        var t = new ChargeTimingTracker();
        t.Feed(At(0), None, 50);                      // 只有未充电观测

        var r = t.Report(At(1));
        Assert.False(r.HasCycle);
        Assert.Null(r.DischargeOrigin);
        Assert.Equal("充电 —", ChargeTimingFormat.ChargeText(r));
        Assert.Equal("放电 —", ChargeTimingFormat.DischargeText(r));
    }

    [Fact]
    public void IsTicking_TrueWhileChargingOrDischarging()
    {
        var t = new ChargeTimingTracker();
        t.Feed(At(0), Charging, 20);
        Assert.True(t.IsTicking);                     // 充电计中
        t.Feed(At(30), Full, 100);
        Assert.False(t.IsTicking);                    // 充满未拔出：两表都不走（时长已定格）
        t.Feed(At(40), None, 100);
        Assert.True(t.IsTicking);                     // 放电计中
    }

    [Fact]
    public void TextFormatting_ChargeAndDischarge()
    {
        var t = new ChargeTimingTracker();
        t.Feed(At(0), None, 3);
        t.Feed(At(10), Charging, 5);
        var r1 = t.Report(At(40));                    // 计中 30 分
        Assert.Equal("充电 30 分（计中，自 5% 起）", ChargeTimingFormat.ChargeText(r1));

        t.Feed(At(80), Charging, 100);
        t.Feed(At(110), Full, 100);
        t.Feed(At(130), None, 99);
        var r2 = t.Report(At(130));
        Assert.Equal("充电 到100% 1.17 h; PMU充满 1.67 h（总 2 h）",
            ChargeTimingFormat.ChargeText(r2));
        Assert.Equal("放电 0 分（自充满取下·计中）", ChargeTimingFormat.DischargeText(r2));

        var r3 = t.Report(At(250));                   // 放电 2 h
        Assert.Equal("放电 2 h（自充满取下·计中）", ChargeTimingFormat.DischargeText(r3));
    }

    [Fact]
    public void NewCycleOverwrites_PlaceholderStatesForLatestOnly()
    {
        var t = new ChargeTimingTracker();
        t.Feed(At(0), None, 80);
        t.Feed(At(10), Charging, 85);
        t.Feed(At(40), None, 90);                     // 第一周期：中止 30 分
        t.Feed(At(50), Charging, 88);
        t.Feed(At(80), Charging, 100);                // 第二周期：显示口径 30 分
        t.Feed(At(100), Full, 100);                   // PMU 口径 50 分
        t.Feed(At(120), None, 99);

        var r = t.Report(At(121));
        Assert.False(r.ChargeAborted);                // 只显示最近周期，第一周期中止态被覆盖
        Assert.Equal(TimeSpan.FromMinutes(30), r.ToDisplayFull);
        Assert.Equal(TimeSpan.FromMinutes(50), r.ToPmuFull);
        Assert.Equal(TimeSpan.FromMinutes(1), r.DischargeElapsed);
        Assert.Equal("自充满取下", r.DischargeOrigin);
    }
}
