// <copyright file="MeasurementToolingTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Dsp;

namespace PencilBox.Tests;

/// <summary>
/// 延迟测量底座的纯数学件（box/07 §2）：脉冲定位精度、信封判读、tick 统计口径。
/// 全部用合成序列自证——设备端未接入前先把 PC 侧算法的可信度钉死。
/// </summary>
public class MeasurementToolingTests
{
    // ================= PulseTrain / PulseLocator =================

    [Fact]
    public void PulseLocator_FindsPulsesWithinOneSample()
    {
        const int rate = 16000;
        var template = PulseTrain.GenerateChirp(rate, 15);
        var (signal, positions) = PulseTrain.Build(template, rate, 20, 500);

        // 模拟"到达"：整段信号前移 120 样本（7.5ms），前后留余量
        var delay = 120;
        var captured = new short[signal.Length + 2 * delay];
        Array.Copy(signal, 0, captured, delay, signal.Length);

        var hits = PulseLocator.Locate(captured, template, 20, rate * 500 / 1000 * 3 / 4);

        Assert.Equal(20, hits.Count);
        for (var k = 0; k < 20; k++)
        {
            var expected = positions[k] + delay;
            Assert.True(Math.Abs(hits[k].PositionSamples - expected) <= 1,
                $"脉冲 {k}：期望 {expected}，实测 {hits[k].PositionSamples}");
            Assert.True(hits[k].Quality > 0.95f, $"脉冲 {k} 质量 {hits[k].Quality} 应接近 1");
        }
    }

    [Fact]
    public void PulseLocator_SurvivesNoiseAndAmplitudeScaling()
    {
        const int rate = 16000;
        var template = PulseTrain.GenerateChirp(rate, 15);
        var (signal, positions) = PulseTrain.Build(template, rate, 5, 500);

        // 室内底噪（~-31dBFS RMS 白噪）+ 脉冲衰减到 1/4（远场/弱声路）
        var rng = new Random(42);
        var captured = new short[signal.Length + 400];
        for (var i = 0; i < captured.Length; i++)
            captured[i] = (short)(rng.Next(-1500, 1500));
        for (var i = 0; i < signal.Length; i++)
            captured[i + 200] += (short)(signal[i] / 4);

        var hits = PulseLocator.Locate(captured, template, 5, rate * 500 / 1000 * 3 / 4, minQuality: 0.4f);

        Assert.Equal(5, hits.Count);
        for (var k = 0; k < 5; k++)
            Assert.True(Math.Abs(hits[k].PositionSamples - (positions[k] + 200)) <= 2,
                $"噪声+衰减下脉冲 {k} 定位漂移超 2 样本");
    }

    [Fact]
    public void PulseLocator_SkipsWeakOrMissingPulses()
    {
        const int rate = 16000;
        var template = PulseTrain.GenerateChirp(rate, 15);
        var (signal, _) = PulseTrain.Build(template, rate, 4, 500);

        // 抹掉第 3 个脉冲 → 只应找到 3 个
        var interval = rate * 500 / 1000;
        Array.Clear(signal, 2 * interval, template.Length);

        var hits = PulseLocator.Locate(signal, template, 4, interval * 3 / 4);
        Assert.Equal(3, hits.Count);
    }

    // ================= EnvelopeAnalyzer =================

    [Fact]
    public void Envelope_FirstSoundTailAndSpeechCount()
    {
        const int rate = 16000;
        // 0~1s 静音 | 1~2s 语音(正弦 -20dBFS) | 2~3s 静音 | 3~3.5s 语音 | 3.5~4s 静音
        var samples = new short[rate * 4];
        FillSine(samples, rate * 1, rate * 2, 3277);        // -20dBFS
        FillSine(samples, rate * 3, rate * 7 / 2, 3277);

        var report = EnvelopeAnalyzer.Analyze(samples, rate);

        Assert.True(report.PeakDbfs is > -21 and < -19, $"峰值 {report.PeakDbfs} 应约 -20dBFS");
        Assert.NotNull(report.FirstSoundMs);
        Assert.True(Math.Abs(report.FirstSoundMs.Value - 1000) <= 100, $"首声 {report.FirstSoundMs}ms 应约 1000ms");
        Assert.NotNull(report.TailMs);
        Assert.True(Math.Abs(report.TailMs.Value - 500) <= 100, $"尾音 {report.TailMs}ms 应约 500ms");
        Assert.Equal(15, report.SpeechBlocks);              // 1s + 0.5s = 1.5s @100ms 块
        Assert.True(Math.Abs(report.SpeechSeconds - 1.5) < 0.01);
    }

    [Fact]
    public void Envelope_AllSilence_HasNoFirstSound()
    {
        var report = EnvelopeAnalyzer.Analyze(new short[16000 * 2], 16000);
        Assert.Null(report.FirstSoundMs);
        Assert.Null(report.TailMs);
        Assert.Equal(EnvelopeAnalyzer.SilenceDbfs, report.PeakDbfs);
        Assert.Equal(0, report.SpeechBlocks);
    }

    private static void FillSine(short[] buf, int from, int to, short amp)
    {
        for (var i = from; i < to; i++)
            buf[i] = (short)Math.Round(Math.Sin(2 * Math.PI * 440 * i / 16000.0) * amp);
    }

    // ================= TickStatsAnalyzer =================

    [Fact]
    public void TickStats_ConstantDelayAndDrift_RetrievesBoth()
    {
        // 真实延迟 12.5ms；笔端钟每天慢 20ms（斜率 1 - 20/86400000... 用可感知量：-50ms/min）
        // arrival = t*(1+drift) + 12.5 + jitter
        const double trueDelay = 12.5;
        const double driftPerMin = -50.0;
        const int n = 400;
        var ticks = new double[n];
        var arrivals = new double[n];
        var tickPeriodMs = 1000.0 / 32768;
        var rng = new Random(7);
        var tMs = 0.0;
        for (var i = 0; i < n; i++)
        {
            ticks[i] = Math.Round(tMs / tickPeriodMs);
            arrivals[i] = tMs * (1 + driftPerMin / 60000.0) + trueDelay + (rng.NextDouble() - 0.5) * 0.4;
            tMs += 15;                                       // 每 15ms 一个样本
        }

        var report = TickStatsAnalyzer.Analyze(ticks, arrivals);

        var s = Assert.Single(report.Sessions);
        Assert.Equal(n, s.Count);
        Assert.True(Math.Abs(s.BaseDelayMs - trueDelay) < 0.3, $"基线 {s.BaseDelayMs} 应约 {trueDelay}ms");
        Assert.True(Math.Abs(s.DriftMsPerMin - driftPerMin) < 5, $"漂移 {s.DriftMsPerMin} 应约 {driftPerMin}ms/min");
        Assert.True(s.P95Ms - s.BaseDelayMs < 1.0, "漂移扣除后 p95−p50 应 <1ms");
        Assert.Equal(0, s.RetransSamples);
    }

    [Fact]
    public void TickStats_DetectsRetransmissionStepsAndWraps()
    {
        const double stepMs = 7.5;
        const int n = 300;
        var ticks = new double[n];
        var arrivals = new double[n];
        var tickPeriodMs = 1000.0 / 32768;
        var rng = new Random(11);
        var tMs = 0.0;
        for (var i = 0; i < n; i++)
        {
            // 10% 样本带 +1 阶跃；tick16 在 2s 处回绕（wrap 65536 → 2000ms）
            var raw = Math.Round(tMs / tickPeriodMs) % 65536;
            ticks[i] = raw;
            var step = (i % 10 == 0) ? stepMs : 0;
            arrivals[i] = tMs + 8 + step + (rng.NextDouble() - 0.5) * 0.3;
            tMs += 15;
        }

        var report = TickStatsAnalyzer.Analyze(ticks, arrivals);

        var s = Assert.Single(report.Sessions);
        Assert.Equal(n, s.Count);
        Assert.Equal(30, s.RetransSamples);                  // i%10==0 的 30 个样本
        Assert.True(Math.Abs(s.RetransRatio - 0.10) < 0.01);
        Assert.True(s.MaxSteps >= 1);
        Assert.True(Math.Abs(s.BaseDelayMs - 8) < 0.5, $"回绕解开且基线 {s.BaseDelayMs} 应约 8ms");
    }

    [Fact]
    public void TickStats_SplitsSessionsOnGap()
    {
        // 两段各 20 样本，中间 500ms 空档
        var ticks = new List<double>();
        var arrivals = new List<double>();
        var tickPeriodMs = 1000.0 / 32768;
        var tMs = 0.0;
        for (var seg = 0; seg < 2; seg++)
        {
            for (var i = 0; i < 20; i++)
            {
                ticks.Add(Math.Round(tMs / tickPeriodMs));
                arrivals.Add(tMs + 10);
                tMs += 15;
            }
            tMs += 500;
        }

        var report = TickStatsAnalyzer.Analyze(ticks, arrivals);
        Assert.Equal(2, report.Sessions.Count);
        Assert.All(report.Sessions, s => Assert.Equal(20, s.Count));
    }
}
