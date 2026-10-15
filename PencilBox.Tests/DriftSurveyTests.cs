// <copyright file="DriftSurveyTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Protocol.Company;

namespace PencilBox.Tests;

/// <summary>
/// 零漂统计模式（对齐卷 06 §4.1 stillsurvey）：双通道分布统计/判静止门/分阶段/温漂回归/决策链/确定性。
/// 定点口径：0.01dps 原值域计算（raw_gx=-123 → -1.23dps）。
/// </summary>
public class DriftSurveyTests
{
    private static (long Ms, TelemetryFrame F) S(long ms, short gx = 0, short gz = 0,
        short rateYaw = 0, short rateRoll = 0, byte flags = 0, short temp = 0) =>
        (ms, new TelemetryFrame((ushort)(ms / 20), flags, gx, 0, gz, 0, 0, 0, rateYaw, rateRoll, 0, 0, temp));

    // ================= 分布统计精度（确定性解析解） =================

    [Fact]
    public void Compute_KnownDistribution_Exact()
    {
        // gx = -10,-8,...,+10（11 个等差奇数样本）：均值 0、中位 0、min/max ±10
        var values = Enumerable.Range(0, 11).Select(i => (i - 5) * 2).ToList();
        var s = DriftSurvey.Compute("raw_gx", values);

        Assert.Equal(11, s.Count);
        Assert.Equal(-10, s.Min);
        Assert.Equal(10, s.Max);
        Assert.Equal(0.0, s.Mean, 6);
        Assert.Equal(0.0, s.Median, 6);
        // P5 = 0.05×10=0.5 → 插值 -10+0.5×2=-9；P95=9.5 → +9
        Assert.Equal(-9.0, s.P5, 6);
        Assert.Equal(9.0, s.P95, 6);
        // RMS（对零）= sqrt(Σv²/n) = sqrt(2×(4+16+36+64+100)/11)
        double rms = Math.Sqrt(2 * (4 + 16 + 36 + 64 + 100) / 11.0);
        Assert.Equal(rms, s.Rms, 6);
        Assert.Equal(rms, s.Sigma, 6);   // 均值 0 → σ=RMS
        Assert.Equal(0.0, s.MeanDps, 8); // dps 换算单点
    }

    [Fact]
    public void Compute_BiasOffset_MeanTracksMedian()
    {
        // 恒偏 -123（-1.23dps）：mean=median=-123，σ=0
        var s = DriftSurvey.Compute("rate_yaw", Enumerable.Repeat(-123, 50));
        Assert.Equal(-123.0, s.Mean, 6);
        Assert.Equal(-123.0, s.Median, 6);
        Assert.Equal(0.0, s.Sigma, 6);
        Assert.Equal(-1.23, s.MeanDps, 8);
    }

    // ================= 补偿点（P5~P95 截尾均值） =================

    [Fact]
    public void Compute_WinsorMean_RobustAgainstOutliers()
    {
        // [0×5, 100]：中位=0、均值=16.67（被离群拉偏）、P95=75 → 截尾均值=(5×0+75)/6=12.5——重尾下贴中位
        var s = DriftSurvey.Compute("raw_gx", new[] { 0, 0, 0, 0, 0, 100 });
        Assert.Equal(0, s.Median, 6);
        Assert.Equal(100.0 / 6, s.Mean, 6);
        Assert.Equal(12.5, s.Winsor, 6);
    }

    [Fact]
    public void Compute_WinsorMean_SymmetricClean_EqualsMean()
    {
        // 对称无离群（高斯近似）：钳位不削任何样本 → 截尾均值==均值（正态下均值最优、零效率损失）
        var s = DriftSurvey.Compute("raw_gz", Enumerable.Range(0, 11).Select(i => (i - 5) * 2));
        Assert.Equal(s.Mean, s.Winsor, 6);
        Assert.Equal(0.0, s.WinsorDps, 8);
    }

    // ================= 判静止门（冻结剔除 + rate 门） =================

    [Fact]
    public void Analyze_Gate_RejectsMovingAndFrozen()
    {
        var samples = new List<(long, TelemetryFrame)>
        {
            S(0, gx: -100),                                  // 静止 ✓
            S(20, rateYaw: 100),                             // rate=1dps < 0.5 门？100=1.00dps ≥50 → 剔
            S(40, rateRoll: -60),                            // 剔（0.6dps）
            S(60, flags: TelemetryFlags.Frozen),             // 剔（冻结）
            S(80, gx: -104, gz: 12, rateYaw: 3, rateRoll: -4), // 静止 ✓
        };

        var r = DriftSurvey.Analyze(samples, new DriftSurvey.Options(GateDps: 0.5));

        Assert.Equal(5, r.TotalFrames);
        Assert.Equal(2, r.AcceptedFrames);
        Assert.Equal(3, r.RejectedFrames);
        Assert.Equal(0.6, r.RejectRatio, 6);
        Assert.Equal(-104, r.Gx!.Min);
        Assert.Equal(-100, r.Gx!.Max);     // 只有 2 帧：-100 与 -104
        Assert.Equal(0, r.Gz!.Min);
        Assert.Equal(12, r.Gz!.Max);
    }

    [Fact]
    public void Analyze_AllRejected_ZeroAccepted_ReportNotStats()
    {
        var samples = Enumerable.Range(0, 10)
            .Select(i => S(i * 20L, rateYaw: 500)).ToList();   // 全在动
        var r = DriftSurvey.Analyze(samples);

        Assert.Equal(0, r.AcceptedFrames);
        Assert.Null(r.Gx);
        Assert.Empty(r.Stages);
        Assert.False(r.ResidualExceedsGuard);   // 无样本不出决策
    }

    // ================= 双通道 + 残余决策链 =================

    [Fact]
    public void Analyze_ResidualMean_GuardDecision()
    {
        // 残余（rate）mean=+8（0.08dps）> 守卫 0.05 → 超标；raw 全量 mean=-200（-2dps）
        var samples = Enumerable.Range(0, 100)
            .Select(i => S(i * 20L, gx: -200, gz: 50, rateYaw: 8, rateRoll: 2)).ToList();
        var r = DriftSurvey.Analyze(samples);

        Assert.True(r.ResidualExceedsGuard);
        Assert.Equal(8.0, r.RateYaw!.Mean, 6);
        Assert.Equal(0.08, r.RateYaw.MeanDps, 8);
        Assert.Equal(-2.0, r.Gx!.MeanDps, 8);
        Assert.Equal(0.5, r.Gz!.MeanDps, 8);
    }

    [Fact]
    public void Analyze_ResidualWithinGuard_NoAdvice()
    {
        var samples = Enumerable.Range(0, 40)
            .Select(i => S(i * 20L, rateYaw: 3, rateRoll: -4)).ToList();   // ≤0.05dps
        Assert.False(DriftSurvey.Analyze(samples).ResidualExceedsGuard);
    }

    // ================= 三方对照（BiasContext 直通） =================

    [Fact]
    public void Analyze_BiasContext_PassedThrough()
    {
        var bias = new DriftSurvey.BiasContext(-0.12f, 0.03f, -0.11f, null);
        var r = DriftSurvey.Analyze(new[] { S(0, gx: 10) }, bias: bias);

        Assert.Same(bias, r.Bias);
        Assert.Equal(-0.12f, r.Bias!.RamGx);
        Assert.Null(r.Bias.NvdsGz);
    }

    // ================= 分阶段（等样本数切窗） =================

    [Fact]
    public void Analyze_Stages_EqualSplit_AndTrend()
    {
        // 90 帧 3 阶段：gx 中位 -30 → 0 → +30（看收敛趋势）
        var samples = new List<(long, TelemetryFrame)>();
        for (int i = 0; i < 90; i++)
        {
            short gx = (short)(i < 30 ? -30 : i < 60 ? 0 : 30);
            samples.Add(S(i * 20L, gx: gx));
        }

        var r = DriftSurvey.Analyze(samples, new DriftSurvey.Options(Stages: 3));

        Assert.Equal(3, r.Stages.Count);
        Assert.All(r.Stages, s => Assert.Equal(30, s.Frames));
        Assert.Equal(-30, r.Stages[0].Gx.Median, 6);
        Assert.Equal(0, r.Stages[1].Gx.Median, 6);
        Assert.Equal(30, r.Stages[2].Gx.Median, 6);
        // 阶段时间窗单调
        Assert.True(r.Stages[0].EndMs <= r.Stages[1].StartMs);
        Assert.True(r.Stages[1].EndMs <= r.Stages[2].StartMs);
    }

    // ================= 温漂回归（v2 temp 通道） =================

    [Fact]
    public void Analyze_TempRegression_SlopeRecovered()
    {
        // 温度 26→28°C，gz = 0.02dps/°C×t + 1dps（+100 固定偏置）→ 斜率 0.02
        var samples = Enumerable.Range(0, 200).Select(i =>
        {
            double t = 26.0 + 2.0 * i / 199;
            short temp = (short)Math.Round(t * 100);
            short gz = (short)Math.Round((0.02 * t + 1.0) * 100);
            return S(i * 20L, gz: gz, temp: temp);
        }).ToList();

        var r = DriftSurvey.Analyze(samples);

        Assert.True(r.TempRegressionValid);
        Assert.Equal(2.0, r.TempSpanC, 6);
        // 定点量化（0.01°C/0.01dps）引入 ~5e-4 斜率噪声，3 位小数断言
        Assert.Equal(0.02, r.TempSlopeGzDpsPerC, 3);
    }

    [Fact]
    public void Analyze_TempSpanTooSmall_RegressionSkipped()
    {
        // v1 帧 temp 恒 0 → 跨度 0 < 0.5 → 不回归（防伪斜率）
        var samples = Enumerable.Range(0, 50).Select(i => S(i * 20L, gx: 10, temp: 0)).ToList();
        var r = DriftSurvey.Analyze(samples);

        Assert.False(r.TempRegressionValid);
        Assert.Equal(0.0, r.TempSpanC);
    }

    // ================= 确定性（06 §4.1 验收：同一录波重跑数值一致） =================

    [Fact]
    public void Analyze_Deterministic_SameInputSameReport()
    {
        var samples = Enumerable.Range(0, 300).Select(i => S(i * 20L,
            gx: (short)((i * 37) % 23 - 11),
            gz: (short)((i * 53) % 31 - 15),
            rateYaw: (short)(i % 7),
            rateRoll: (short)-(i % 5),
            temp: (short)(2600 + i / 10))).ToList();

        var a = DriftSurvey.Analyze(samples, new DriftSurvey.Options(Stages: 4));
        var b = DriftSurvey.Analyze(samples, new DriftSurvey.Options(Stages: 4));

        Assert.Equal(a.AcceptedFrames, b.AcceptedFrames);
        Assert.Equal(a.Gx, b.Gx);
        Assert.Equal(a.Gz, b.Gz);
        Assert.Equal(a.RateYaw, b.RateYaw);
        Assert.Equal(a.RateRoll, b.RateRoll);
        Assert.Equal(a.Stages, b.Stages);
        Assert.Equal(a.TempSlopeGxDpsPerC, b.TempSlopeGxDpsPerC);
    }

    // ================= CSV 读取（TelemetryCsv.ReadAll 单点） =================

    [Fact]
    public void TelemetryCsv_ReadAll_V2AndV1Rows_AndDirtyLines()
    {
        var csv = string.Join("\n",
            TelemetryCsv.Header,
            "100,1,0,-100,0,50,0,0,1000,3,-4,0,0,2650",
            "120,2,0,1,0,0,0,0,0,0,0,0,0",           // v1 13 列
            "garbage,xxx",
            "# meta: fps=50.0,dropped=0,div=1,fw=0.13.24",
            "");
        var path = Path.Combine(Path.GetTempPath(), $"still_{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, csv);
        try
        {
            var rows = TelemetryCsv.ReadAll(path);
            Assert.Equal(2, rows.Count);
            Assert.Equal(100, rows[0].PcRxMs);
            Assert.Equal(-100, rows[0].Frame.RawGx);
            Assert.Equal(2650, rows[0].Frame.TempImu);
            Assert.Equal(0, rows[1].Frame.TempImu);     // v1 行 temp 缺省 0
            Assert.Equal(-4, rows[0].Frame.RateRoll);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
