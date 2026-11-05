// <copyright file="OtaAutoRetryGateTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Fsm;

namespace PencilBox.Tests;

/// <summary>
/// 非满块失败"自动整会话重试 1 次"闸门（OtaResult.ShouldAutoRetrySession）决策事实。
/// 背景=2026-11-05 指挥官并发真机：1.0.3 固件收块确认本底不稳（单台重查 7~22 次/204 块
/// 概率波动），并发放大后同块 3 连查 → 固件 DCMD=03 主动放弃；失败后手动重跑成功实证
/// 重开会话有效。满块失败不重试（10-22 裁决：满块走复核不重传）。
/// </summary>
public class OtaAutoRetryGateTests
{
    private static OtaResult Res(OtaOutcome outcome, int done, int total) =>
        new(outcome, total == 0 ? 0 : done * 100 / total, done, total, "msg");

    [Fact]
    public void Failed_midway_retries() // 事件形状：设备中途放弃（0x84/204，DCMD=03）
        => Assert.True(Res(OtaOutcome.Failed, 0x54, 204).ShouldAutoRetrySession);

    [Fact]
    public void Failed_at_first_blocks_retries() // 开始帧不久即放弃（极少块）也重试
        => Assert.True(Res(OtaOutcome.Failed, 1, 204).ShouldAutoRetrySession);

    [Fact]
    public void Failed_after_full_blocks_does_not_retry() // 满块 Failed 走复核（ff0b4f1），绝不重传
        => Assert.False(Res(OtaOutcome.Failed, 204, 204).ShouldAutoRetrySession);

    [Fact]
    public void Failed_with_zero_total_does_not_retry() // 退化保护：无块数信息不重试
        => Assert.False(Res(OtaOutcome.Failed, 0, 0).ShouldAutoRetrySession);

    [Fact]
    public void Terminal_success_does_not_retry() // 成功/Skip 无重试语义
    {
        Assert.False(Res(OtaOutcome.Succeeded, 204, 204).ShouldAutoRetrySession);
        Assert.False(Res(OtaOutcome.Skipped, 0, 204).ShouldAutoRetrySession);
    }

    [Fact]
    public void Unconfirmed_and_aborted_do_not_retry() // Unconfirmed=满块未知（复核轨）；Aborted=验证性中断
    {
        Assert.False(Res(OtaOutcome.Unconfirmed, 196, 196).ShouldAutoRetrySession);
        Assert.False(Res(OtaOutcome.Aborted, 50, 204).ShouldAutoRetrySession);
    }
}
