// <copyright file="TickStatsAnalyzer.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Dsp;

/// <summary>一个会话段（session）的 P2 tick 统计结果</summary>
public sealed record TickSessionStats(
    int Index,
    int Count,
    double BaseDelayMs,          // 漂移扣除后的分布中心（p50）
    double P95Ms,
    double MinMs,
    double MaxMs,
    double DriftMsPerMin,        // 拟合出的钟差漂移（正=笔端钟快）
    int RetransSamples,          // 相对 p50 抬升 > 0.6×步进 的样本数（+7.5n 阶跃 = 重传）
    double RetransRatio,
    int MaxSteps);               // 观测到的最大阶跃级数（round((max-p50)/step)）

/// <summary>P2 带内 tick 全量统计（笔端 02 §4 / Dongle D5 口径）</summary>
public sealed record TickReport(
    int TotalSamples,
    double StepMs,
    IReadOnlyList<TickSessionStats> Sessions);

/// <summary>
/// P2 带内时间戳统计：输入 (tick16, 本地到达时刻) 序列，按会话切段，
/// 每段最小二乘线性拟合扣除双端晶振漂移（±100ppm ≈ ±6ms/分钟），输出单向延迟分布与重传率。
/// 纯数学，仿真序列自测（box/07 §2.4）。
/// </summary>
public static class TickStatsAnalyzer
{
    /// <param name="rawTicks">笔端 16-bit tick 原值（32kHz，2s 回绕；回绕在此函数内解开）</param>
    /// <param name="arrivalMs">PC 侧到达时刻（毫秒，任意时间基准）</param>
    /// <param name="tickPeriodMs">单 tick 时长（1000/32768）</param>
    /// <param name="wrapTicks">回绕周期（65536）</param>
    /// <param name="sessionGapMs">tick 间隔超过此值判为新会话</param>
    /// <param name="stepMs">阶跃粒度（连接间隔 7.5ms）</param>
    public static TickReport Analyze(
        IReadOnlyList<double> rawTicks,
        IReadOnlyList<double> arrivalMs,
        double tickPeriodMs = 1000.0 / 32768,
        double wrapTicks = 65536,
        double sessionGapMs = 200,
        double stepMs = 7.5)
    {
        if (rawTicks.Count != arrivalMs.Count) throw new ArgumentException("tick 与 arrival 数量不一致");
        if (rawTicks.Count == 0) return new TickReport(0, stepMs, []);

        // 解 16-bit 回绕：tick 突降超过半个周期视为回绕
        var t = new double[rawTicks.Count];
        t[0] = rawTicks[0] * tickPeriodMs;
        var wrapMs = wrapTicks * tickPeriodMs;
        var wraps = 0.0;
        for (var i = 1; i < rawTicks.Count; i++)
        {
            var prev = rawTicks[i - 1];
            var cur = rawTicks[i];
            if (prev - cur > wrapTicks / 2) wraps += wrapMs;
            t[i] = cur * tickPeriodMs + wraps;
        }

        // 按间隙切段
        var sessions = new List<List<int>>();
        var current = new List<int> { 0 };
        for (var i = 1; i < t.Length; i++)
        {
            if (t[i] - t[i - 1] > sessionGapMs)
            {
                sessions.Add(current);
                current = new List<int>();
            }
            current.Add(i);
        }
        sessions.Add(current);

        var stats = new List<TickSessionStats>();
        foreach (var (idxs, k) in sessions.Select((idxs, k) => (idxs, k)))
            stats.Add(AnalyzeSession(k, idxs, t, arrivalMs, stepMs));

        return new TickReport(t.Length, stepMs, stats);
    }

    private static TickSessionStats AnalyzeSession(int index, List<int> idxs, double[] t, IReadOnlyList<double> arrival, double stepMs)
    {
        // 最小二乘：arrival ≈ a·t + b（斜率差 = 双端钟差漂移）
        double st = 0, sa = 0;
        foreach (var i in idxs) { st += t[i]; sa += arrival[i]; }
        var meanT = st / idxs.Count;
        var meanA = sa / idxs.Count;
        double stt = 0, sta = 0;
        foreach (var i in idxs)
        {
            var dt = t[i] - meanT;
            stt += dt * dt;
            sta += dt * (arrival[i] - meanA);
        }
        var slope = stt < 1e-9 ? 1.0 : sta / stt;             // 单点/共线段退化为 a=1
        var driftPerMin = (slope - 1.0) * 60000.0;

        // 漂移扣除后的延迟分布
        var delays = new double[idxs.Count];
        for (var j = 0; j < idxs.Count; j++)
            delays[j] = arrival[idxs[j]] - slope * t[idxs[j]];

        var sorted = (double[])delays.Clone();
        Array.Sort(sorted);
        var p50 = Percentile(sorted, 0.50);
        var p95 = Percentile(sorted, 0.95);

        var retrans = 0;
        foreach (var d in delays)
            if (d - p50 > 0.6 * stepMs) retrans++;

        var maxSteps = (int)Math.Floor((sorted[^1] - p50) / stepMs);

        return new TickSessionStats(
            index,
            idxs.Count,
            p50, p95,
            sorted[0], sorted[^1],
            driftPerMin,
            retrans,
            idxs.Count == 0 ? 0 : retrans / (double)idxs.Count,
            Math.Max(0, maxSteps));
    }

    private static double Percentile(double[] sorted, double q)
    {
        if (sorted.Length == 0) return 0;
        var pos = q * (sorted.Length - 1);
        var lo = (int)Math.Floor(pos);
        var hi = (int)Math.Ceiling(pos);
        return lo == hi ? sorted[lo] : sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }
}
