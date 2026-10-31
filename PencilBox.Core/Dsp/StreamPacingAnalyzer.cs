// <copyright file="StreamPacingAnalyzer.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Dsp;

/// <summary>流走表剖面结果（ms 口径）。</summary>
/// <param name="FittedStepMs">拟合步长（每序号实际步进 ms）</param>
/// <param name="Ppm">步长相对标称的钟差（百万分率；双端晶振差量级）</param>
/// <param name="P50DevMs">偏差 p50（传输基线之上的典型积压）</param>
/// <param name="P95DevMs">偏差 p95（突发积压水位）</param>
/// <param name="MaxDevMs">偏差 max（窗口内最深积压）</param>
/// <param name="Frames">参与拟合的帧数</param>
public sealed record StreamPacingStats(
    double FittedStepMs, double Ppm, double P50DevMs, double P95DevMs, double MaxDevMs, long Frames);

/// <summary>
/// 流走表剖面（软件接收器 probe 的链路质量口径）：
/// 帧到达 = a + b·序号 线性拟合，偏差 = 到达 − 拟合值。
/// 偏差分布即 BLE+Windows 传输/缓冲的活剖面：p50≈基线积压、max≈窗口内最深积压；
/// b 对标称步长的偏离 = 双端钟差 PPM。
/// 前提：序号须已解回绕（发端步进单调；调用方按 (cur−prev+256)%256 累计）。
/// </summary>
public static class StreamPacingAnalyzer
{
    public static StreamPacingStats Analyze(IReadOnlyList<(long Seq, double ArrivalMs)> frames, double nominalStepMs)
    {
        if (frames.Count < 2)
            throw new ArgumentException("至少 2 帧才能拟合走表", nameof(frames));
        if (nominalStepMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(nominalStepMs));

        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var (seq, arr) in frames)
        {
            sx += seq;
            sy += arr;
            sxx += (double)seq * seq;
            sxy += seq * (double)arr;
        }
        var n = (double)frames.Count;
        var denom = n * sxx - sx * sx;
        if (Math.Abs(denom) < 1e-9)
            throw new ArgumentException("序号无差异，无法拟合", nameof(frames));

        var b = (n * sxy - sx * sy) / denom;
        var a = (sy - b * sx) / n;

        var devs = frames.Select(f => f.ArrivalMs - (a + b * f.Seq)).OrderBy(x => x).ToList();
        double Pick(double frac) => devs[(int)Math.Clamp((long)(frac * (devs.Count - 1)), 0, devs.Count - 1)];

        return new StreamPacingStats(
            b,
            (b / nominalStepMs - 1) * 1e6,
            Pick(0.5),
            Pick(0.95),
            devs[^1],
            frames.Count);
    }
}
