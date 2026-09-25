// <copyright file="PulseLocator.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Dsp;

/// <summary>互相关定位出的一个脉冲到达点</summary>
public sealed record PulseHit(int PositionSamples, float Quality);

/// <summary>
/// 离线脉冲定位：模板与采集流的归一化互相关（延迟标定 box/03 §3.2-3）。
/// 粗扫用步长（默认 0.25ms）+ 滑动窗口能量归一，找到候选峰后抛物线内插到样本级，
/// 再按最小间隔贪心取前 N 个峰——不要求实时，只求一次性测量准。
/// </summary>
public static class PulseLocator
{
    /// <param name="signal">采集流（16-bit PCM，单声道）</param>
    /// <param name="template">单脉冲模板（与 signal 同采样率；时长不同率同即可）</param>
    /// <param name="expected">期望脉冲数（找到这么多即停）</param>
    /// <param name="minSeparationSamples">峰间最小间隔（一般取脉冲间隔的 3/4）</param>
    /// <param name="minQuality">峰显著度下限（归一化相关 0~1）</param>
    public static List<PulseHit> Locate(short[] signal, short[] template, int expected,
        int minSeparationSamples, float minQuality = 0.4f, int? strideSamples = null)
    {
        if (signal.Length < template.Length) return [];
        if (template.Length < 16) throw new ArgumentOutOfRangeException(nameof(template), "模板太短，相关性无意义");
        var stride = Math.Max(1, strideSamples ?? Math.Max(1, template.Length / 64));

        var m = template.Length;
        var n = signal.Length;

        // 模板归一
        var tpl = new float[m];
        double tplEnergy = 0;
        for (var i = 0; i < m; i++)
        {
            tpl[i] = template[i] / 32768f;
            tplEnergy += (double)tpl[i] * tpl[i];
        }
        var tplRms = Math.Sqrt(tplEnergy / m);
        if (tplRms < 1e-6) throw new ArgumentException("模板是纯静音，无法做相关", nameof(template));

        // 滑动窗口能量（前缀和）
        var prefix = new double[n + 1];
        for (var i = 0; i < n; i++)
        {
            var x = signal[i] / 32768.0;
            prefix[i + 1] = prefix[i] + x * x;
        }

        // 粗扫归一化互相关
        var lastStart = n - m;
        var coarseCount = lastStart / stride + 1;
        var corr = new float[coarseCount];
        for (var c = 0; c < coarseCount; c++)
        {
            var off = c * stride;
            double dot = 0;
            for (var i = 0; i < m; i++)
                dot += (double)tpl[i] * (signal[off + i] / 32768.0);
            var winEnergy = prefix[off + m] - prefix[off];
            var den = Math.Sqrt(winEnergy * tplEnergy);
            corr[c] = (float)(den < 1e-12 ? 0 : dot / den);
        }

        // 贪心取峰：全局最大 → 记录 → 屏蔽 ±minSeparation → 重复；
        // 质量在抛物线内插后的精确位置重算（粗扫格点差半格能掉到 0.6，虚低）
        var hits = new List<PulseHit>();
        var taken = new bool[coarseCount];
        var maskCoarse = Math.Max(1, minSeparationSamples / stride);
        while (hits.Count < expected)
        {
            var best = -1;
            var bestVal = float.NegativeInfinity;
            for (var c = 0; c < coarseCount; c++)
            {
                if (taken[c] || corr[c] <= bestVal) continue;
                bestVal = corr[c];
                best = c;
            }
            if (best < 0 || bestVal < minQuality) break;

            // 屏蔽邻近粗格点
            for (var c = Math.Max(0, best - maskCoarse); c <= Math.Min(coarseCount - 1, best + maskCoarse); c++)
                taken[c] = true;

            var (pos, quality) = Refine(signal, tpl, prefix, tplEnergy, best * stride, stride);
            hits.Add(new PulseHit(pos, quality));
        }

        hits.Sort((a, b) => a.PositionSamples.CompareTo(b.PositionSamples));
        return hits;
    }

    /// <summary>在 ±stride 邻域逐样本精扫 + 抛物线内插到样本级；返回峰位与该位置的归一化相关</summary>
    private static (int Position, float Quality) Refine(short[] signal, float[] tpl, double[] prefix,
        double tplEnergy, int coarseOffset, int stride)
    {
        var m = tpl.Length;
        var from = Math.Max(0, coarseOffset - stride);
        var to = Math.Min(signal.Length - m, coarseOffset + stride);

        var bestOff = coarseOffset;
        double bestDot = double.NegativeInfinity;
        var dots = new Dictionary<int, double>();
        for (var off = from; off <= to; off++)
        {
            double dot = 0;
            for (var i = 0; i < m; i++)
                dot += (double)tpl[i] * (signal[off + i] / 32768.0);
            dots[off] = dot;
            if (dot > bestDot) { bestDot = dot; bestOff = off; }
        }

        // 抛物线内插：需要左右邻点
        var pos = bestOff;
        if (dots.TryGetValue(bestOff - 1, out var l) && dots.TryGetValue(bestOff + 1, out var r))
        {
            var denom = l - 2 * bestDot + r;
            if (Math.Abs(denom) > 1e-12)
            {
                var delta = 0.5 * (l - r) / denom;             // -1..1
                if (delta is > -1.0 and < 1.0)
                    pos = (int)Math.Round(bestOff + delta);
            }
        }

        // 精确位置的归一化相关（窗口能量走前缀和）
        double dotP = 0;
        for (var i = 0; i < m; i++)
            dotP += (double)tpl[i] * (signal[pos + i] / 32768.0);
        var winEnergy = prefix[pos + m] - prefix[pos];
        var den = Math.Sqrt(winEnergy * tplEnergy);
        return (pos, (float)(den < 1e-12 ? 0 : dotP / den));
    }
}
