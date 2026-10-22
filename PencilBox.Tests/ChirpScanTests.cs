// <copyright file="ChirpScanTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Dsp;

namespace PencilBox.Tests;

/// <summary>
/// 2b chirp 扫描（t7 场 2 PC 半边，规格单 t7/Dongle/D2 §1）：
/// idx 模式滞留样点级还原 / 缺窗报告 / PPM 拟合 / 网格模式原点-周期拟合与缺失槽位 / 抗噪 / 输入校验。
/// </summary>
public class ChirpScanTests
{
    private const int Rate = 16000;

    private static short[] Insert(short[] signal, long pos)
    {
        var tpl = ChirpTemplate.Table;
        for (var i = 0; i < tpl.Length; i++)
        {
            var p = pos + i;
            if (p >= 0 && p < signal.Length)
                signal[(int)p] = (short)Math.Clamp(signal[p] + tpl[i], short.MinValue, short.MaxValue);
        }
        return signal;
    }

    private static short[] Noisy(short[] signal, double rms, int seed)
    {
        var rng = new Random(seed);
        for (var i = 0; i < signal.Length; i++)
        {
            // 12 个均匀和近似高斯（Irwin–Hall），均值 0、std=rms
            var g = 0.0;
            for (var k = 0; k < 12; k++) g += rng.NextDouble() * 2 - 1;
            signal[i] = (short)Math.Clamp(signal[i] + g * rms / Math.Sqrt(4), short.MinValue, short.MaxValue);
        }
        return signal;
    }

    // ================= 模板（规格单 §1 整数表） =================

    [Fact]
    public void Template_MatchesSpec()
    {
        Assert.Equal(120, ChirpTemplate.Table.Length);
        Assert.Equal(12000, ChirpTemplate.Table.Max(Math.Abs));
        Assert.Equal(0, ChirpTemplate.Table[0]);
        Assert.Equal(12000, ChirpTemplate.PeriodSamples);
        Assert.Equal(16000, ChirpTemplate.SampleRate);
    }

    // ================= idx 模式 =================

    [Fact]
    public void ScanByIdx_KnownLags_SampleExact()
    {
        var signal = new short[Rate * 4];
        long[] idx = [24000, 36000, 48000];
        int[] lag = [160, 176, 128];   // 10ms / 11ms / 8ms
        for (var i = 0; i < idx.Length; i++) Insert(signal, idx[i] + lag[i]);

        var report = ChirpScanAnalyzer.ScanByIdx(signal, Rate, idx);

        Assert.Equal("idx", report.Mode);
        Assert.Equal(3, report.DetectedCount);
        Assert.Empty(report.MissingSlots);
        for (var i = 0; i < 3; i++)
        {
            var h = report.Hits.First(x => x.Slot == i);
            Assert.Equal(idx[i] + lag[i], h.PeakPos);                        // 样点级还原
            Assert.Equal(lag[i] * 1000.0 / Rate, h.LagMs!.Value, 3);
            Assert.True(h.Quality >= 0.99f, $"Q={h.Quality}");
        }
        Assert.NotNull(report.Lag);
        Assert.Equal(8.0, report.Lag!.MinMs, 3);
        Assert.Equal(11.0, report.Lag!.MaxMs, 3);
        // 注：滞留变化与钟漂在斜率里纠缠，变滞留场景不判 PPM（判测量=滞留分布本身）
    }

    [Fact]
    public void ScanByIdx_SilentWindow_ReportedMissing()
    {
        var signal = new short[Rate * 4];
        long[] idx = [24000, 36000, 48000];
        Insert(signal, idx[0] + 100);
        Insert(signal, idx[2] + 100);      // 槽 1 缺失（窗内纯静音）

        var report = ChirpScanAnalyzer.ScanByIdx(signal, Rate, idx);

        Assert.Equal(2, report.DetectedCount);
        Assert.Equal([1], report.MissingSlots);
    }

    [Fact]
    public void ScanByIdx_OutOfRangeIdx_CountsMissing()
    {
        var signal = new short[Rate];
        var report = ChirpScanAnalyzer.ScanByIdx(signal, Rate, [999999]);

        Assert.Equal(0, report.DetectedCount);
        Assert.Equal([0], report.MissingSlots);
    }

    [Fact]
    public void ScanByIdx_SpeechBandNoise_StillDetected()
    {
        var signal = new short[Rate * 4];
        long[] idx = [24000, 36000, 48000, 60000];
        foreach (var i in idx) Insert(signal, i + 150);
        Noisy(signal, rms: 3000, seed: 42);   // 噪声 RMS 3000 vs 模板 RMS≈8500

        var report = ChirpScanAnalyzer.ScanByIdx(signal, Rate, idx);

        Assert.Equal(4, report.DetectedCount);
        Assert.All(report.Hits, h => Assert.True(h.Quality >= 0.5f, $"Q={h.Quality}"));
        foreach (var h in report.Hits)
            Assert.True(Math.Abs(h.PeakPos - (h.Idx!.Value + 150)) <= 1, $"峰位漂移 {h.PeakPos - (h.Idx.Value + 150)}");
        // 恒定滞留 → PPM≈0（噪声致峰位 ±1 样点 → ppm 误差 ~80 内）
        Assert.NotNull(report.Ppm);
        Assert.True(Math.Abs(report.Ppm.Value) < 100, $"PPM={report.Ppm}");
    }

    // ================= 网格模式 =================

    [Fact]
    public void ScanByGrid_PeriodicChirps_FitAndMissingSlots()
    {
        var signal = new short[Rate * 8];          // 128000 样点 ≈ 10 槽
        for (var k = 0; k < 10; k++)
        {
            if (k is 3 or 6) continue;             // 中段缺 2 窗
            Insert(signal, 5000 + k * ChirpTemplate.PeriodSamples + 200);
        }

        var report = ChirpScanAnalyzer.ScanByGrid(signal, Rate, ChirpTemplate.PeriodSamples);

        Assert.Equal("grid", report.Mode);
        Assert.Equal(10, report.ExpectedCount);
        Assert.Equal(8, report.DetectedCount);
        Assert.Equal([3, 6], report.MissingSlots);
        Assert.NotNull(report.Ppm);
        Assert.True(Math.Abs(report.Ppm.Value) < 50, $"PPM={report.Ppm}");
        Assert.All(report.Hits, h => Assert.True(Math.Abs(h.GridDevSamples!.Value) <= 1));
    }

    [Fact]
    public void ScanByGrid_ClockDrift_PpmRecovered()
    {
        // 基站时钟快 500ppm：周期 12000 → 12006（PC 侧每槽多等 6 样点）
        var signal = new short[Rate * 8];
        for (var k = 0; k < 10; k++) Insert(signal, 4000 + (long)Math.Round(k * 12006.0) + 300);

        var report = ChirpScanAnalyzer.ScanByGrid(signal, Rate, ChirpTemplate.PeriodSamples);

        Assert.Equal(10, report.DetectedCount);
        Assert.Empty(report.MissingSlots);
        Assert.NotNull(report.Ppm);
        Assert.True(Math.Abs(report.Ppm.Value - 500) < 10, $"PPM={report.Ppm}（期望≈500）");
    }

    [Fact]
    public void ScanByGrid_NoChirps_EmptyReport()
    {
        var signal = Noisy(new short[Rate * 2], rms: 800, seed: 7);
        var report = ChirpScanAnalyzer.ScanByGrid(signal, Rate, ChirpTemplate.PeriodSamples);

        Assert.Equal(0, report.DetectedCount);
        Assert.Empty(report.Hits);
    }

    [Fact]
    public void ScanByGrid_TrailingPartialSlot_NotCounted()
    {
        // 录音在最后一窗后中途截断：末槽之后的"不存在"槽不得报缺失
        var signal = new short[Rate * 4];          // 64000 样点：4 槽多一点
        for (var k = 0; k < 4; k++) Insert(signal, 2000 + k * ChirpTemplate.PeriodSamples);

        var report = ChirpScanAnalyzer.ScanByGrid(signal, Rate, ChirpTemplate.PeriodSamples);

        Assert.Equal(4, report.ExpectedCount);
        Assert.Equal(4, report.DetectedCount);
        Assert.Empty(report.MissingSlots);
    }
}
