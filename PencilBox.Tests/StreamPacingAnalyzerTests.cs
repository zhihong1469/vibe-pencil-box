// <copyright file="StreamPacingAnalyzerTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Dsp;

namespace PencilBox.Tests;

/// <summary>流走表剖面单测：理想钟/钟差/突发积压三形态。</summary>
public class StreamPacingAnalyzerTests
{
    [Fact]
    public void PerfectPacing_ZeroDeviations_ZeroPpm()
    {
        // 到达 = seq * 7.5ms，无抖动
        var frames = Enumerable.Range(0, 100).Select(i => ((long)i, i * 7.5)).ToList();
        var s = StreamPacingAnalyzer.Analyze(frames, 7.5);
        Assert.Equal(7.5, s.FittedStepMs, 6);
        Assert.Equal(0, s.Ppm, 3);
        Assert.Equal(0, s.P50DevMs, 6);
        Assert.Equal(0, s.MaxDevMs, 6);
        Assert.Equal(100, s.Frames);
    }

    [Fact]
    public void ClockDrift_PpmReflectsStepRatio()
    {
        // 步长 7.5 × 1.00075（=750ppm 精确值 7.505625ms）→ PPM=750
        var frames = Enumerable.Range(0, 400).Select(i => ((long)i, i * 7.505625)).ToList();
        var s = StreamPacingAnalyzer.Analyze(frames, 7.5);
        Assert.Equal(750, s.Ppm, 3);
    }

    [Fact]
    public void BurstBacklog_ShowsInMaxNotInP50()
    {
        // 理想走表 + 每 50 帧一次 20ms 突发积压（后续帧不累积：积压只在单帧体现）
        var frames = new List<(long, double)>();
        for (var i = 0; i < 300; i++)
            frames.Add((i, i * 7.5 + (i % 50 == 0 ? 20.0 : 0.0)));
        var s = StreamPacingAnalyzer.Analyze(frames, 7.5);
        // 6/300 离群点会让最小二乘斜率偏 ~0.0013ms，步长只断言到 2 位小数
        Assert.Equal(7.5, s.FittedStepMs, 2);
        Assert.True(s.MaxDevMs > 15, $"max={s.MaxDevMs}");
        Assert.True(s.P50DevMs < s.MaxDevMs / 2, $"p50={s.P50DevMs} 应显著低于 max");
    }

    [Fact]
    public void TooFewFrames_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            StreamPacingAnalyzer.Analyze(new List<(long, double)> { (0, 0) }, 7.5));
    }
}
