// <copyright file="OtaRecheckGateTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Fsm;

namespace PencilBox.Tests;

/// <summary>
/// 会话后"主动探测读版本对拍"收口闸门（OtaResult.ShouldRecheckAfterSession）决策事实。
/// 背景=2026-11-02 键盘真机三台并行：满块后终态丢失，R2 开始帧超时×3 判 Failed，
/// 引擎早退不复核报失败，稍后实读三台均已到目标版本——满块 Failed 必须同进复核。
/// </summary>
public class OtaRecheckGateTests
{
    private static OtaResult Res(OtaOutcome outcome, int done, int total) =>
        new(outcome, total == 0 ? 0 : done * 100 / total, done, total, "msg");

    [Fact]
    public void Failed_after_full_blocks_rechecks() // 事件形状：满块 196/196 + 终态丢失判 Failed
        => Assert.True(Res(OtaOutcome.Failed, 196, 196).ShouldRecheckAfterSession);

    [Fact]
    public void Failed_midway_does_not_recheck() // 未满块失败（中途断/真不受理）直接失败，不烧探测窗
        => Assert.False(Res(OtaOutcome.Failed, 50, 196).ShouldRecheckAfterSession);

    [Fact]
    public void Failed_with_zero_total_does_not_recheck() // 退化保护：无块数信息不进复核
        => Assert.False(Res(OtaOutcome.Failed, 0, 0).ShouldRecheckAfterSession);

    [Fact]
    public void Unconfirmed_rechecks() // 满块后设备重受理（既有 10-22 裁决路径）保持复核
        => Assert.True(Res(OtaOutcome.Unconfirmed, 196, 196).ShouldRecheckAfterSession);

    [Fact]
    public void Terminal_success_rechecks() // 成功/Skip 走常规复核（既有行为不变）
    {
        Assert.True(Res(OtaOutcome.Succeeded, 196, 196).ShouldRecheckAfterSession);
        Assert.True(Res(OtaOutcome.Skipped, 0, 196).ShouldRecheckAfterSession);
    }

    [Fact]
    public void Aborted_does_not_recheck() // 验证性中断留旧固件，版本永远不达标，不进复核
        => Assert.False(Res(OtaOutcome.Aborted, 196, 196).ShouldRecheckAfterSession);
}
