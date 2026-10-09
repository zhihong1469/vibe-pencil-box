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

    /// <summary>
    /// 脉冲串格点跟踪定位（抗反射/抗噪声版）：
    /// 所有过阈模板匹配位置都作为候选格点原点（各容 j·interval 前移），逐原点做全槽位确认评分，
    /// 确认数最多者胜；平分取更早的原点（直达必先于其反射）。然后逐槽在 ±<paramref name="windowSamples"/>
    /// 窗口内精扫取峰——窗外的反射峰、噪声与孤立瞬态（如采集端 AGC 起动）天然被排除。
    /// 返回与槽位一一对应的列表；质量不足或越界的槽位为 null。
    /// </summary>
    public static List<PulseHit?> LocateTracked(short[] signal, short[] template, int expectedCount,
        int intervalSamples, int windowSamples, float minQuality = 0.4f)
    {
        if (windowSamples < 1) throw new ArgumentOutOfRangeException(nameof(windowSamples));
        var n = signal.Length;
        var m = template.Length;

        // 前缀和窗口能量 + 模板归一（候选收集与逐槽精扫共用）
        var prefix = new double[n + 1];
        for (var i = 0; i < n; i++)
        {
            var x = signal[i] / 32768.0;
            prefix[i + 1] = prefix[i] + x * x;
        }
        var tpl = new float[m];
        var tplEnergy = 0.0;
        for (var i = 0; i < m; i++)
        {
            tpl[i] = template[i] / 32768f;
            tplEnergy += (double)tpl[i] * tpl[i];
        }
        if (tplEnergy < 1e-12) throw new ArgumentException("模板是纯静音，无法做相关", nameof(template));

        // 候选原点：前 8 个过阈匹配位置 × j∈{0,1}（j 容错头几个脉冲缺失），近重复去重
        var crossings = CollectCrossings(signal, tpl, prefix, tplEnergy, m, minQuality, maxCount: 8);
        if (crossings.Count == 0) return [];
        var origins = new List<long>();
        foreach (var c in crossings)
            for (var j = 0; j < 2; j++)
            {
                var o = (long)c - j * intervalSamples;
                if (o >= 0 && origins.All(x => Math.Abs(x - o) > windowSamples / 2))
                    origins.Add(o);
            }

        // 逐候选全槽确认评分；平分取更早原点
        long bestOrigin = origins[0];
        var bestScore = -1;
        foreach (var origin in origins)
        {
            var score = 0;
            for (var k = 0; k < expectedCount; k++)
            {
                var center = origin + k * (long)intervalSamples;
                var from = (int)Math.Max(0, center - windowSamples);
                var to = (int)Math.Min(n - m, center + windowSamples);
                if (to < from) continue;
                var q = SlotQuality(signal, tpl, prefix, tplEnergy, m, from, to);
                if (q >= minQuality) score++;
            }
            if (score > bestScore || (score == bestScore && origin < bestOrigin))
            {
                bestScore = score;
                bestOrigin = origin;
            }
        }

        // 最终逐槽精扫
        var slots = new List<PulseHit?>();
        for (var k = 0; k < expectedCount; k++)
        {
            var center = bestOrigin + k * (long)intervalSamples;
            var from = (int)Math.Max(0, center - windowSamples);
            var to = (int)Math.Min(n - m, center + windowSamples);
            if (to < from) { slots.Add(null); continue; }

            var pos = SlotPeak(signal, tpl, m, from, to, out var bestDot);
            var winEnergy = prefix[pos + m] - prefix[pos];
            var den = Math.Sqrt(winEnergy * tplEnergy);
            var quality = den < 1e-12 ? 0 : bestDot / den;
            slots.Add(quality >= minQuality ? new PulseHit(pos, (float)quality) : null);
        }
        return slots;
    }

    /// <summary>粗步扫收集归一化相关过阈的位置（升序，最多 maxCount 个）</summary>
    private static List<int> CollectCrossings(short[] signal, float[] tpl, double[] prefix,
        double tplEnergy, int templateLength, float minQuality, int maxCount)
    {
        var n = signal.Length;
        var m = templateLength;
        var stride = Math.Max(1, m / 64);
        var found = new List<int>();
        for (var off = 0; off <= n - m && found.Count < maxCount; off += stride)
        {
            double dot = 0;
            for (var i = 0; i < m; i++)
                dot += (double)tpl[i] * (signal[off + i] / 32768.0);
            var den = Math.Sqrt((prefix[off + m] - prefix[off]) * tplEnergy);
            if (den >= 1e-12 && dot / den >= minQuality * 0.9)
            {
                found.Add(off);
                off += m;                               // 跳过本匹配的延续区，避免同峰连报
            }
        }
        return found;
    }

    /// <summary>[from,to] 内逐样本精扫，返回最佳点积位置</summary>
    private static int SlotPeak(short[] signal, float[] tpl, int m, int from, int to, out double bestDot)
    {
        bestDot = double.NegativeInfinity;
        var bestPos = from;
        for (var off = from; off <= to; off++)
        {
            double dot = 0;
            for (var i = 0; i < m; i++)
                dot += (double)tpl[i] * (signal[off + i] / 32768.0);
            if (dot > bestDot) { bestDot = dot; bestPos = off; }
        }
        return bestPos;
    }

    /// <summary>[from,to] 内最佳位置的归一化相关</summary>
    private static double SlotQuality(short[] signal, float[] tpl, double[] prefix,
        double tplEnergy, int m, int from, int to)
    {
        var pos = SlotPeak(signal, tpl, m, from, to, out var bestDot);
        var winEnergy = prefix[pos + m] - prefix[pos];
        var den = Math.Sqrt(winEnergy * tplEnergy);
        return den < 1e-12 ? 0 : bestDot / den;
    }

    /// <summary>在 ±stride 邻域逐样本精扫 + 抛物线内插到样本级；返回峰位与该位置的归一化相关</summary>
    private static (int Position, float Quality) Refine(short[] signal, float[] tpl, double[] prefix,
        double tplEnergy, int coarseOffset, int stride)    {
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
