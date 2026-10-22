// <copyright file="ProbeEdgeRobustnessTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Dsp;
using Xunit.Abstractions;

namespace PencilBox.Tests;

/// <summary>
/// t7 B-2 预研件 1 收口：共享模式重采样鲁棒性边缘参数，离线合成跑批（生产同路径：
/// PulseTrain.Build 铺脉冲 → 已知延迟+高斯噪声 → PulseLocator.LocateTracked 定位，质量门 0.4）。
/// 噪声底 = chirp 段 RMS 为基准的 SNR（Irwin–Hall 近高斯，种子 20261022 确定性）。
/// 两个形态：clean 组=硬断言（12/12 样点级还原）；噪声组=标定快照（当天 25 格跑批的
/// 检出数快照，分析器改进后数字变化须有意识更新快照）。报告=t7/Box/ B-2 收口文。
/// </summary>
public class ProbeEdgeRobustnessTests(ITestOutputHelper output)
{
    private const int PulseCount = 12;
    private const int IntervalMs = 500;
    private const int PulseMs = 15;
    private const int LeadInMs = 250;
    private const double TrueLatencyMs = 156.2;   // 用权威实测值当已知延迟

    public static TheoryData<int> Rates() => [8000, 16000, 44100, 48000, 96000];

    /// <summary>噪声组快照（2026-10-22 跑批定标）：检出数/12。质量门 0.4 的可靠下沿=0dB；-6dB 为崖沿。</summary>
    public static TheoryData<int, double, int> NoiseSnapshot()
    {
        var td = new TheoryData<int, double, int>();
        foreach (var (rate, snr, located) in new[]
        {
            (8000, 0.0, 12), (8000, -6.0, 9), (8000, -8.0, 3), (8000, -12.0, 0),
            (16000, 0.0, 12), (16000, -6.0, 10), (16000, -8.0, 3), (16000, -12.0, 0),
            (44100, 0.0, 12), (44100, -6.0, 0), (44100, -8.0, 0), (44100, -12.0, 0),
            (48000, 0.0, 11), (48000, -6.0, 11), (48000, -8.0, 0), (48000, -12.0, 0),
            (96000, 0.0, 12), (96000, -6.0, 11), (96000, -8.0, 0), (96000, -12.0, 0),
        })
            td.Add(rate, snr, located);
        return td;
    }

    [Theory]
    [MemberData(nameof(Rates))]
    public void Clean_AllRates_SampleExact(int rate)
    {
        var (located, minQ, maxErrMs) = Run(rate, snrDb: null);
        output.WriteLine($"rate={rate} snr=clean → located={located}/{PulseCount} minQ={minQ:F3} maxDelayErr={maxErrMs:F3}ms");
        Assert.Equal(PulseCount, located);
        Assert.True(minQ >= 0.9f, $"minQ={minQ:F3}");
        Assert.True(maxErrMs <= 1.0, $"延迟还原误差 {maxErrMs:F3}ms");
    }

    [Theory]
    [MemberData(nameof(NoiseSnapshot))]
    public void NoiseFloor_CalibratedSnapshot(int rate, double snrDb, int expectedLocated)
    {
        var (located, minQ, maxErrMs) = Run(rate, snrDb);
        output.WriteLine($"rate={rate} snr={snrDb:F0}dB → located={located}/{PulseCount} minQ={minQ:F3} maxDelayErr={maxErrMs:F3}ms");
        Assert.Equal(expectedLocated, located);
        if (located == PulseCount)
        {
            // 全检出时延迟必须样点级还原；缺槽组的错位（±500ms 整间隔）是已立案的噪声病理
            Assert.True(maxErrMs <= 1.0, $"全检出但延迟还原误差 {maxErrMs:F3}ms");
            Assert.True(minQ >= 0.4f, $"质量门失守 minQ={minQ:F3}");
        }
    }

    private (int Located, double MinQ, double MaxErrMs) Run(int rate, double? snrDb)
    {
        var chirp = PulseTrain.GenerateChirp(rate, PulseMs);
        var (rendered, positions) = PulseTrain.Build(chirp, rate, PulseCount, IntervalMs, LeadInMs);

        // 采集侧 = 渲染信号整体延迟已知延迟（+1s 尾余防末脉冲截断），可选定噪
        var delaySamples = (int)Math.Round(TrueLatencyMs / 1000.0 * rate);
        var captured = new short[rendered.Length + delaySamples + rate];
        for (var i = 0; i < rendered.Length; i++)
            captured[i + delaySamples] = rendered[i];
        if (snrDb is { } snr)
        {
            var chirpRms = Math.Sqrt(chirp.Average(v => (double)v * v));
            var noiseRms = chirpRms * Math.Pow(10, -snr / 20.0);   // 负 SNR=噪声更强
            var rng = new Random(20261022);
            for (var i = 0; i < captured.Length; i++)
            {
                var g = 0.0;
                for (var k = 0; k < 12; k++) g += rng.NextDouble() * 2 - 1;
                captured[i] = (short)Math.Clamp(captured[i] + g * noiseRms / 2.0, short.MinValue, short.MaxValue);
            }
        }

        // 生产同参（LatencyProbeEngine.Analyze 的核心调用）
        var intervalSamples = rate * IntervalMs / 1000;
        var windowSamples = Math.Min(rate * 20 / 1000, intervalSamples / 4);
        var slots = PulseLocator.LocateTracked(captured, chirp, PulseCount, intervalSamples, windowSamples);

        var located = 0;
        double minQ = 1;
        var maxErrMs = 0.0;
        // LocateTracked 候选 Crossing 全灭时返回空表（生产 Analyze 以 Math.Min 兜底，台架同防）
        for (var k = 0; k < Math.Min(slots.Count, PulseCount); k++)
        {
            if (slots[k] is not { } hit) continue;
            located++;
            minQ = Math.Min(minQ, hit.Quality);
            // 到达−发出：采集内相对渲染位 k 的位移应恰为已知延迟
            var delayMs = (hit.PositionSamples - positions[k]) * 1000.0 / rate;
            maxErrMs = Math.Max(maxErrMs, Math.Abs(delayMs - TrueLatencyMs));
        }
        return (located, minQ, maxErrMs);
    }
}
