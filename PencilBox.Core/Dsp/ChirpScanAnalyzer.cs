// <copyright file="ChirpScanAnalyzer.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Dsp;

/// <summary>单个 chirp 窗的定位结果（idx 模式含滞留，网格模式含栅格偏差）</summary>
public sealed record ChirpHit(
    int Slot,
    long PeakPos,
    float Quality,
    long? Idx,
    double? LagMs,
    double? GridDevSamples);

/// <summary>滞留分布（ms，仅 idx 模式）</summary>
public sealed record ChirpLagStats(double MinMs, double P50Ms, double MaxMs);

/// <summary>chirpscan 汇总报告</summary>
public sealed record ChirpScanReport(
    string Mode,
    int SampleRate,
    int ExpectedCount,
    int DetectedCount,
    IReadOnlyList<int> MissingSlots,
    double? Ppm,
    ChirpLagStats? Lag,
    IReadOnlyList<ChirpHit> Hits);

/// <summary>
/// 2b chirp 扫描（t7 场 2 判读的 PC 半边，规格单 t7/Dongle/D2 §1 为唯一输入）：
/// 120 点 1k→3k up-chirp 模板（固件同源整数表 <see cref="ChirpTemplate.Table"/>）与
/// UAC 录音的归一化互相关，峰门限 0.5（规格单 §2）。
/// idx 模式：按 [CHIRP] 串口行 idx 列表在 ±窗内找峰，滞留 ms=(峰位−idx)·1000/rate；
/// grid 模式：无串口时按 750ms 网格自拟合（原点+周期线性回归→PPM+缺失槽位）。
/// 口径注：idx 自 boot 计数、峰位自录音起点计数，绝对滞留含录制起点常量偏置——
/// 同一场录音内的滞留分布形状与 PPM 才是有效观测量，跨场对账勿直接比绝对值。
/// </summary>
public static class ChirpScanAnalyzer
{
    public const int DefaultWindowSamples = 400;   // ±400 样点（±25ms），规格单 §2
    public const float DefaultMinQuality = 0.5f;

    /// <summary>idx 模式：逐 [CHIRP] idx 在 ±windowSamples 内样点级找峰</summary>
    public static ChirpScanReport ScanByIdx(short[] signal, int rate, IReadOnlyList<long> idxList,
        int windowSamples = DefaultWindowSamples, float minQuality = DefaultMinQuality)
    {
        if (windowSamples < 1) throw new ArgumentOutOfRangeException(nameof(windowSamples));
        if (idxList.Count == 0) throw new ArgumentException("idx 列表为空", nameof(idxList));

        var (tpl, tplEnergy, prefix) = Prepare(signal);
        var hits = new List<ChirpHit>();
        var missing = new List<int>();
        for (var s = 0; s < idxList.Count; s++)
        {
            var idx = idxList[s];
            var (pos, q) = BestInWindow(signal, tpl, tplEnergy, prefix, idx - windowSamples, idx + windowSamples);
            if (pos < 0 || q < minQuality) { missing.Add(s); continue; }
            hits.Add(new ChirpHit(s, pos, (float)q, idx, (pos - idx) * 1000.0 / rate, null));
        }

        double? ppm = null;
        if (hits.Count >= 2)
        {
            // 峰位对 idx 的最小二乘斜率 = PC 每收 1 个基站样点前进多少样点；偏 1 的部分即时钟漂移
            var (_, b) = LinearFit(hits.Select(h => (double)h.Idx!.Value).ToList(),
                                   hits.Select(h => (double)h.PeakPos).ToList());
            ppm = (b - 1) * 1e6;
        }
        return new ChirpScanReport("idx", rate, idxList.Count, hits.Count, missing, ppm, LagOf(hits), hits);
    }

    /// <summary>
    /// 网格模式（无串口兜底）：粗扫收峰 → 中位间隔估周期 → 峰位对槽号线性回归拟合原点与周期
    /// （PPM 对 750ms 标称）→ 逐槽 ±窗精扫。首末检出槽之间才算缺失——录音边界外本就看不到。
    /// </summary>
    public static ChirpScanReport ScanByGrid(short[] signal, int rate, int periodSamples,
        int windowSamples = DefaultWindowSamples, float minQuality = DefaultMinQuality)
    {
        if (windowSamples < 1) throw new ArgumentOutOfRangeException(nameof(windowSamples));
        if (periodSamples < 1) throw new ArgumentOutOfRangeException(nameof(periodSamples));

        var (tpl, tplEnergy, prefix) = Prepare(signal);
        var n = signal.Length;
        var m = tpl.Length;

        // 1) 粗扫归一化互相关，贪心取峰、屏蔽 ±3/4 周期。
        //    步长必须 ≤ 主瓣宽（≈1/带宽=0.5ms=8 样点）：chirp 自相关对 30 样点错位即近零
        //    （时宽带宽积 15 的脉冲压缩特性），m/4 步长会整体跨过峰——与 PulseLocator 同用 m/64。
        var stride = Math.Max(1, m / 64);
        var lastStart = n - m;
        if (lastStart < 0) return new ChirpScanReport("grid", rate, 0, 0, [], null, null, []);
        var coarseCount = lastStart / stride + 1;
        var corr = new float[coarseCount];
        for (var c = 0; c < coarseCount; c++)
        {
            var off = c * stride;
            double dot = 0;
            for (var i = 0; i < m; i++)
                dot += (double)tpl[i] * (signal[off + i] / 32768.0);
            var den = Math.Sqrt((prefix[off + m] - prefix[off]) * tplEnergy);
            corr[c] = (float)(den < 1e-12 ? 0 : dot / den);
        }
        var mask = Math.Max(1, periodSamples * 3 / 4 / stride);
        var maxPeaks = n / periodSamples + 8;
        var taken = new bool[coarseCount];
        var coarsePeaks = new List<int>();
        while (coarsePeaks.Count < maxPeaks)
        {
            var best = -1;
            var bv = float.NegativeInfinity;
            for (var c = 0; c < coarseCount; c++)
            {
                if (!taken[c] && corr[c] > bv) { bv = corr[c]; best = c; }
            }
            if (best < 0 || bv < minQuality) break;
            for (var c = Math.Max(0, best - mask); c <= Math.Min(coarseCount - 1, best + mask); c++)
                taken[c] = true;
            coarsePeaks.Add(best * stride);
        }
        if (coarsePeaks.Count < 2) return new ChirpScanReport("grid", rate, 0, 0, [], null, null, []);

        // 2) 逐峰精扫到样点级
        var refined = coarsePeaks
            .Select(p => BestInWindow(signal, tpl, tplEnergy, prefix, p - stride, p + stride))
            .Where(r => r.Pos >= 0)
            .Select(r => r.Pos)
            .OrderBy(x => x)
            .ToList();

        // 3) 中位间隔估周期 → 槽号指派 → 峰位对槽号回归（原点 a + 实测周期 b）
        var diffs = new List<double>();
        for (var i = 1; i < refined.Count; i++) diffs.Add(refined[i] - refined[i - 1]);
        diffs.Sort();
        var p0 = diffs[diffs.Count / 2];
        var ks = refined.Select(p => Math.Round((p - (double)refined[0]) / p0)).ToList();
        var (a, b) = LinearFit(ks, refined.Select(p => (double)p).ToList());
        var ppm = (b / periodSamples - 1) * 1e6;

        // 4) 首末检出槽之间逐槽 ±窗精扫
        var kLo = (int)ks.Min();
        var kHi = (int)ks.Max();
        var hits = new List<ChirpHit>();
        var missing = new List<int>();
        for (var k = kLo; k <= kHi; k++)
        {
            var center = (long)Math.Round(a + b * (k - kLo));
            var (pos, q) = BestInWindow(signal, tpl, tplEnergy, prefix, center - windowSamples, center + windowSamples);
            if (pos < 0 || q < minQuality) { missing.Add(k); continue; }
            hits.Add(new ChirpHit(k, pos, (float)q, null, null, pos - center));
        }
        return new ChirpScanReport("grid", rate, kHi - kLo + 1, hits.Count, missing, ppm, null, hits);
    }

    private static ChirpLagStats? LagOf(List<ChirpHit> hits)
    {
        if (hits.Count == 0) return null;
        var lags = hits.Select(h => h.LagMs ?? 0).OrderBy(x => x).ToList();
        return new ChirpLagStats(lags[0], lags[lags.Count / 2], lags[^1]);
    }

    private static (float[] Tpl, double TplEnergy, double[] Prefix) Prepare(short[] signal)
    {
        var template = ChirpTemplate.Table;
        var n = signal.Length;
        var prefix = new double[n + 1];
        for (var i = 0; i < n; i++)
        {
            var x = signal[i] / 32768.0;
            prefix[i + 1] = prefix[i] + x * x;
        }
        var m = template.Length;
        var tpl = new float[m];
        double e = 0;
        for (var i = 0; i < m; i++)
        {
            tpl[i] = template[i] / 32768f;
            e += (double)tpl[i] * tpl[i];
        }
        if (e < 1e-12) throw new InvalidOperationException("chirp 模板是纯静音，无法做相关");
        return (tpl, e, prefix);
    }

    /// <summary>[from,to] 闭区间逐样点精扫，返回归一化相关峰位与峰显著度；区间无效返回 (-1, 0)</summary>
    private static (long Pos, double Quality) BestInWindow(short[] signal, float[] tpl,
        double tplEnergy, double[] prefix, long from, long to)
    {
        var n = signal.Length;
        var m = tpl.Length;
        var lo = Math.Max(0L, from);
        var hi = Math.Min((long)n - m, to);
        if (hi < lo) return (-1, 0);

        long best = lo;
        double bestDot = double.NegativeInfinity;
        for (var off = lo; off <= hi; off++)
        {
            double dot = 0;
            for (var i = 0; i < m; i++)
                dot += (double)tpl[i] * (signal[(int)off + i] / 32768.0);
            if (dot > bestDot) { bestDot = dot; best = off; }
        }
        var winEnergy = prefix[best + m] - prefix[best];
        var den = Math.Sqrt(winEnergy * tplEnergy);
        return (best, den < 1e-12 ? 0 : bestDot / den);
    }

    private static (double A, double B) LinearFit(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
    {
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        for (var i = 0; i < xs.Count; i++)
        {
            sx += xs[i]; sy += ys[i];
            sxx += xs[i] * xs[i]; sxy += xs[i] * ys[i];
        }
        var d = xs.Count * sxx - sx * sx;
        if (Math.Abs(d) < 1e-9) return (sy / xs.Count, 0);
        var b = (xs.Count * sxy - sx * sy) / d;
        return ((sy - b * sx) / xs.Count, b);
    }
}
