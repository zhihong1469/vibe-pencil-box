// <copyright file="AecSilenceGateTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Dsp;

namespace PencilBox.Tests;

/// <summary>
/// AEC 条件触发门：参考静音满保持窗才旁路，任何响动立即恢复（防拖尾回声）。
/// </summary>
public class AecSilenceGateTests
{
    [Fact]
    public void SilentFrames_BypassOnlyAfterHangoverExpires()
    {
        var gate = new AecSilenceGate(hangoverFrames: 50);

        // 静音后保持窗内仍应开门（压住拖尾回声）
        for (var i = 0; i < 50; i++)
            Assert.True(gate.Update(0), $"第 {i} 帧应保持开门");

        // 保持窗耗尽才旁路
        Assert.False(gate.Update(0));
        Assert.False(gate.Update(0));
    }

    [Fact]
    public void LoudFrame_ReopensImmediately()
    {
        var gate = new AecSilenceGate(hangoverFrames: 10);

        for (var i = 0; i < 10; i++) gate.Update(0);
        Assert.False(gate.Update(0));   // 已旁路

        Assert.True(gate.Update(1000)); // 参考一响立即开门
        Assert.True(gate.Update(0));    // 之后重新数保持窗
    }

    [Fact]
    public void Threshold_AtOrAboveMarkedActive()
    {
        var gate = new AecSilenceGate(hangoverFrames: 1);

        Assert.True(gate.Update(AecSilenceGate.SilenceRmsThreshold));
        Assert.True(gate.Update((short)(AecSilenceGate.SilenceRmsThreshold - 1)));   // 保持窗内
        Assert.False(gate.Update((short)(AecSilenceGate.SilenceRmsThreshold - 1)));  // 保持窗耗尽
    }

    [Fact]
    public void Reset_RestartsHangover()
    {
        var gate = new AecSilenceGate(hangoverFrames: 3);

        // 冷启动即开门（满保持窗）：3 帧静音内保持开门，第 4 帧才旁路
        Assert.True(gate.Update(0));
        Assert.True(gate.Update(0));
        Assert.True(gate.Update(0));
        Assert.False(gate.Update(0));

        gate.Reset();
        Assert.True(gate.Update(0));    // 复位后重新给满保持窗
    }
}
