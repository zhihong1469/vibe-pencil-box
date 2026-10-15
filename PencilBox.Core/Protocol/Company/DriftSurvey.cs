// <copyright file="DriftSurvey.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>

namespace PencilBox.Core.Protocol.Company;

/// <summary>
/// `0x62` **零漂统计模式**（对齐卷 06 §4.1 stillsurvey，2026-10-15 用户需求）——纯逻辑无串口依赖，
/// CLI `company stillstats` 与 Desktop「零漂统计」开关共用同一单点；输入=录制样本（在线收流或离线 CSV 回放），
/// 输出=分布统计报告（确定性：同一录波重跑数值一致）。
///
/// 双通道对账：① `raw_gx/raw_gz`（**去偏差前**）=全量零漂分布；② `rate_yaw/rate_roll`（去偏差+滤波后）
/// =补偿后残余零漂。判静止=flags 冻结帧剔除 + rate 峰值 &lt;门限（默认 0.5dps）剔动段；
/// 分位=P5/中位/P95（线性插值）；温度跨度 ≥0.5°C 时联动 gx/gz 温漂回归（dps/°C）。
/// 统计口径：定点 0.01dps 原值域计算，报告双单位（原值 + /100=dps）。
/// </summary>
public static class DriftSurvey
{
    /// <summary>零偏机制 v2 守卫死区（笔端 06 §8.2）：残余零漂 |mean| 超过它 → 建议固化/等守卫自动写</summary>
    public const double GuardDeadzoneDps = 0.05;

    /// <summary>温漂回归生效的最低温度跨度（°C；低于它回归被噪声支配，防伪斜率）</summary>
    public const double MinTempSpanC = 0.5;

    /// <summary>动段剔除占比超过它 → 提示"统计窗被污染建议重跑"（06 §4.1）</summary>
    public const double RejectRatioWarn = 0.20;

    /// <summary>单通道分布统计（0.01dps 定点原值域；Count=参与统计帧数）</summary>
    public sealed record AxisStats(
        string Channel, int Count,
        int Min, int Max,
        double Mean, double Median, double Rms, double Sigma,
        double P5, double P95)
    {
        /// <summary>dps 域（=定点 /100，单点换算）</summary>
        public double MeanDps => Mean / 100.0;

        public double MedianDps => Median / 100.0;
    }

    /// <summary>分阶段统计（等样本数切窗；看 Kalman 收敛/温漂趋势用）</summary>
    public sealed record StageStats(
        int Index, long StartMs, long EndMs, int Frames,
        AxisStats Gx, AxisStats Gz, AxisStats RateYaw, AxisStats RateRoll);

    /// <summary>三方对照上下文：笔端实读 0x07/0x08（RAM 当前零偏）与 0x09/0x0A（NVDS 持久值），dps；读不到=null</summary>
    public sealed record BiasContext(float? RamGx, float? RamGz, float? NvdsGx, float? NvdsGz);

    /// <summary>统计选项（门限 dps / 阶段数）</summary>
    public sealed record Options(double GateDps = 0.5, int Stages = 4);

    /// <summary>统计报告（全窗四通道 + 分阶段 + 温漂数 + 判定）</summary>
    public sealed record Report
    {
        public int TotalFrames { get; init; }
        public int AcceptedFrames { get; init; }
        public int RejectedFrames { get; init; }

        /// <summary>动段/冻结剔除占比（>0.2 提示重跑）</summary>
        public double RejectRatio => TotalFrames == 0 ? 0 : (double)RejectedFrames / TotalFrames;

        public long SpanMs { get; init; }
        public double TempSpanC { get; init; }
        public double TempStartC { get; init; }
        public double TempEndC { get; init; }

        public AxisStats? Gx { get; init; }
        public AxisStats? Gz { get; init; }
        public AxisStats? RateYaw { get; init; }
        public AxisStats? RateRoll { get; init; }

        public IReadOnlyList<StageStats> Stages { get; init; } = Array.Empty<StageStats>();

        /// <summary>温漂数（静止段 gx/gz vs temp_imu 线性回归，dps/°C；跨度不足 <see cref="MinTempSpanC"/> 时 false）</summary>
        public bool TempRegressionValid { get; init; }
        public double TempSlopeGxDpsPerC { get; init; }
        public double TempSlopeGzDpsPerC { get; init; }

        /// <summary>残余零漂 |mean| 超守卫死区（0.05dps）→ true（决策链：可 0x0B=1 固化或等守卫自动写）</summary>
        public bool ResidualExceedsGuard { get; init; }

        public BiasContext? Bias { get; init; }
    }

    /// <summary>
    /// 主入口：样本（时序给定，不重排）→ 静止门过滤 → 全窗/分阶段统计 + 温漂回归 + 决策判定。
    /// 全部 AcceptedFrames=0 时仍返回报告（调用方按 0 判"统计窗被污染作废"）。
    /// </summary>
    public static Report Analyze(
        IReadOnlyList<(long PcRxMs, TelemetryFrame Frame)> samples,
        Options? options = null, BiasContext? bias = null)
    {
        var opt = options ?? new Options();
        var gateFixed = opt.GateDps * 100.0;   // dps → 0.01dps 定点域

        var accepted = new List<(long Ms, TelemetryFrame F)>(samples.Count);
        int rejected = 0;
        foreach (var (ms, f) in samples)
        {
            // 判静止（06 §4.1）：冻结帧剔除 + rate 峰值<门限（rate=去偏差滤波后，动段必然超门）
            bool moving = f.IsFrozen ||
                          Math.Abs(f.RateYaw) >= gateFixed ||
                          Math.Abs(f.RateRoll) >= gateFixed;
            if (moving) rejected++;
            else accepted.Add((ms, f));
        }

        var report = new Report
        {
            TotalFrames = samples.Count,
            AcceptedFrames = accepted.Count,
            RejectedFrames = rejected,
            SpanMs = samples.Count > 0 ? samples[^1].PcRxMs - samples[0].PcRxMs : 0,
            Bias = bias,
        };

        if (accepted.Count == 0)
            return report;

        AxisStats? Stats(string channel, Func<TelemetryFrame, int> pick) =>
            Compute(channel, accepted.Select(a => pick(a.F)));

        report = report with
        {
            Gx = Stats("raw_gx", f => f.RawGx),
            Gz = Stats("raw_gz", f => f.RawGz),
            RateYaw = Stats("rate_yaw", f => f.RateYaw),
            RateRoll = Stats("rate_roll", f => f.RateRoll),
        };

        // 分阶段（等样本数；阶段数自适应上限=样本数）
        int stageCount = Math.Clamp(opt.Stages, 1, accepted.Count);
        var stages = new List<StageStats>(stageCount);
        for (int i = 0; i < stageCount; i++)
        {
            int lo = accepted.Count * i / stageCount;
            int hi = accepted.Count * (i + 1) / stageCount;
            var seg = accepted.GetRange(lo, hi - lo);
            stages.Add(new StageStats(
                i + 1, seg[0].Ms, seg[^1].Ms, seg.Count,
                Compute("raw_gx", seg.Select(a => (int)a.F.RawGx)),
                Compute("raw_gz", seg.Select(a => (int)a.F.RawGz)),
                Compute("rate_yaw", seg.Select(a => (int)a.F.RateYaw)),
                Compute("rate_roll", seg.Select(a => (int)a.F.RateRoll))));
        }
        report = report with { Stages = stages };

        // 温度：跨度 + gx/gz 温漂回归（v2 帧 temp 才有值；v1 恒 0 → 跨度 0 → 不回归）
        var temps = accepted.Select(a => a.F.TempImu * 0.01).ToList();
        double tStart = temps[0], tEnd = temps[^1];
        double tMin = temps.Min(), tMax = temps.Max();
        report = report with { TempStartC = tStart, TempEndC = tEnd, TempSpanC = tMax - tMin };
        if (tMax - tMin >= MinTempSpanC)
        {
            var gxSlope = SlopeDpsPerC(accepted.Select(a => (a.F.TempImu * 0.01, a.F.RawGx * 0.01)));
            var gzSlope = SlopeDpsPerC(accepted.Select(a => (a.F.TempImu * 0.01, a.F.RawGz * 0.01)));
            if (gxSlope.HasValue && gzSlope.HasValue)
                report = report with { TempRegressionValid = true, TempSlopeGxDpsPerC = gxSlope.Value, TempSlopeGzDpsPerC = gzSlope.Value };
        }

        // 决策链：补偿后残余零漂 |mean| 超守卫死区 → 提示固化
        bool exceeds = (report.RateYaw != null && Math.Abs(report.RateYaw.MeanDps) > GuardDeadzoneDps) ||
                       (report.RateRoll != null && Math.Abs(report.RateRoll.MeanDps) > GuardDeadzoneDps);
        report = report with { ResidualExceedsGuard = exceeds };

        return report;
    }

    /// <summary>单通道分布统计：min/max/mean/中位/RMS(对零)/σ/P5/P95；分位=线性插值（确定性）</summary>
    public static AxisStats Compute(string channel, IEnumerable<int> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        double mean = sorted.Average();
        double p5 = Quantile(sorted, 0.05);
        double p50 = Quantile(sorted, 0.50);
        double p95 = Quantile(sorted, 0.95);
        double rms = Math.Sqrt(sorted.Sum(v => (double)v * v) / sorted.Count);
        double sigma = Math.Sqrt(sorted.Sum(v => (v - mean) * (v - mean)) / sorted.Count);
        return new AxisStats(channel, sorted.Count, sorted[0], sorted[^1],
            mean, p50, rms, sigma, p5, p95);
    }

    /// <summary>线性插值分位：pos=p×(n-1)，相邻两序取样插值</summary>
    private static double Quantile(List<int> sorted, double p)
    {
        if (sorted.Count == 1)
            return sorted[0];
        double pos = p * (sorted.Count - 1);
        int lo = (int)Math.Floor(pos);
        int hi = Math.Min(lo + 1, sorted.Count - 1);
        double frac = pos - lo;
        return sorted[lo] * (1 - frac) + sorted[hi] * frac;
    }

    /// <summary>最小二乘斜率（dps/°C）；x 无变化或样本 <2 → null</summary>
    private static double? SlopeDpsPerC(IEnumerable<(double TempC, double Dps)> pts)
    {
        var xs = pts.Select(p => p.TempC).ToList();
        var ys = pts.Select(p => p.Dps).ToList();
        if (xs.Count < 2)
            return null;
        double mx = xs.Average(), my = ys.Average();
        double den = xs.Sum(x => (x - mx) * (x - mx));
        return den == 0 ? null : xs.Zip(ys, (x, y) => (x - mx) * (y - my)).Sum() / den;
    }
}
