// <copyright file="EchoScorer.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Dsp;

/// <summary>单窗回声相似度结论（DelayMs 为相对全局对齐的残余偏移）</summary>
public sealed record EchoScoreWindow(double TimeSec, double PeakCorr, double DelayMs);

/// <summary>回声相似度总报告</summary>
public sealed record EchoScoreReport(
    IReadOnlyList<EchoScoreWindow> Windows,
    double MeanCorr,
    double ActiveCorr,
    int LoopRate,
    double GlobalOffsetMs)
{
    /// <summary>有效窗（峰显著 &gt; 0.2）的平均相关——AEC 评测主指标：越低 = 输出里激励残余越少</summary>
    public double ActiveCorrValue => ActiveCorr;
}

/// <summary>
/// 回声相似度评分（AEC 客观评测，与到达时间无关）：
/// 把环回录音重采样到参考率，按窗在 ±<paramref name="searchSec"/> 内对参考做归一化互相关取峰。
/// 峰值 ≈ 该窗输出中激励的含量（0=无，1=原样拷贝）。AEC 有效 → 直通输出与激励的相关显著下降。
/// </summary>
public static class EchoScorer
{
    public static EchoScoreReport Analyze(short[] loop, int loopRate, short[] reference, int refRate,
        double windowSec = 1.0, double searchSec = 0.5)
    {
        if (loop.Length < 2 || reference.Length < 2) throw new ArgumentException("空音频");
        var rate = refRate;
        var x = loopRate == rate ? loop : ResampleLinear(loop, loopRate, rate, reference.Length);

        var win = (int)(windowSec * rate);
        var search = (int)(searchSec * rate);
        var lagStep = Math.Max(1, rate / 1000);          // 粗扫 1ms
        var windows = new List<EchoScoreWindow>();
        var peaks = new List<double>();

        // 全局对齐：录音与参考起点通常差几秒（先开录音后放音），用 10ms RMS 包络互相关找全局偏移
        var frameSize = Math.Max(1, rate / 100);
        var globalLagFrames = EstimateGlobalLag(x, reference, frameSize);
        var globalLag = globalLagFrames * frameSize;

        // 预计算参考各窗能量
        var refWindows = (reference.Length - win) / win + 1;
        for (var w = 0; w < refWindows; w++)
        {
            var refOff = w * win;
            double refEnergy = 0;
            for (var i = 0; i < win; i++)
                refEnergy += (double)reference[refOff + i] * reference[refOff + i];
            if (refEnergy < 1e6) continue;               // 参考该窗近静音，跳过

            // 在 x 中搜索：锚 = 全局对齐后的参考位置，允许 ±search
            var anchor = refOff + globalLag;
            var from = Math.Max(0, anchor - search);
            var to = Math.Min(x.Length - win, anchor + search + win);
            float bestR = 0;
            var bestLag = 0;
            for (var lag = from; lag <= to; lag += lagStep)
            {
                var r = NormCorr(x, lag, reference, refOff, win, refEnergy);
                if (r > bestR) { bestR = r; bestLag = lag; }
            }
            // 精扫 ±lagStep
            var fineFrom = Math.Max(0, bestLag - lagStep);
            var fineTo = Math.Min(x.Length - win, bestLag + lagStep);
            for (var lag = fineFrom; lag <= fineTo; lag++)
            {
                var r = NormCorr(x, lag, reference, refOff, win, refEnergy);
                if (r > bestR) { bestR = r; bestLag = lag; }
            }

            windows.Add(new EchoScoreWindow(w * windowSec, bestR, (bestLag - anchor) * 1000.0 / rate));
            peaks.Add(bestR);
        }

        var mean = peaks.Count > 0 ? peaks.Average() : 0;
        var active = peaks.Where(p => p > 0.2).ToList();
        return new EchoScoreReport(windows, mean, active.Count > 0 ? active.Average() : 0, rate,
            globalLag * 1000.0 / rate);
    }

    /// <summary>包络互相关估计 loop 相对 reference 的全局偏移（帧数）；无有效能量返回 0</summary>
    private static int EstimateGlobalLag(short[] loop, short[] reference, int frameSize)
    {
        var refEnv = Envelope(reference, frameSize);
        var loopEnv = Envelope(loop, frameSize);
        if (refEnv.Length < 4 || loopEnv.Length < 4) return 0;

        double refSum = 0;
        foreach (var v in refEnv) refSum += v * v;
        if (refSum < 1e-9) return 0;

        // lag = loop 相对 reference 的帧偏移，可行域覆盖两轴所有重叠；帧已 10ms，逐帧步进足够
        var minLag = -(refEnv.Length - 1);
        var maxLag = loopEnv.Length - 1;
        float best = 0;
        var bestLag = 0;
        for (var lag = minLag; lag <= maxLag; lag++)
        {
            double dot = 0, loopEnergy = 0;
            var i0 = Math.Max(0, -lag);
            var i1 = Math.Min(refEnv.Length, loopEnv.Length - lag);
            if (i1 - i0 < 4) continue;
            for (var i = i0; i < i1; i++)
            {
                var a = loopEnv[i + lag];
                var b = refEnv[i];
                dot += a * b;
                loopEnergy += a * a;
            }
            var den = Math.Sqrt(loopEnergy * refSum);
            var r = den < 1e-9 ? 0 : (float)(dot / den);
            if (r > best) { best = r; bestLag = lag; }
        }
        return bestLag;
    }

    private static double[] Envelope(short[] signal, int frameSize)
    {
        var frames = signal.Length / frameSize;
        var env = new double[frames];
        for (var f = 0; f < frames; f++)
        {
            double s = 0;
            var off = f * frameSize;
            for (var i = 0; i < frameSize; i++)
            {
                var v = signal[off + i] / 32768.0;
                s += v * v;
            }
            env[f] = Math.Sqrt(s / frameSize);
        }
        return env;
    }

    private static float NormCorr(short[] x, int xOff, short[] reference, int refOff, int win, double refEnergy)
    {
        double dot = 0, xEnergy = 0;
        for (var i = 0; i < win; i++)
        {
            var a = x[xOff + i];
            var b = reference[refOff + i];
            dot += (double)a * b;
            xEnergy += (double)a * a;
        }
        var den = Math.Sqrt(xEnergy * refEnergy);
        return den < 1e-6 ? 0 : (float)(dot / den);
    }

    /// <summary>线性插值重采样（评分足够；不做抗混叠，激励与录音同为语音带宽）</summary>
    private static short[] ResampleLinear(short[] input, int fromRate, int toRate, int hintLength)
    {
        var outLen = (long)input.Length * toRate / fromRate;
        var output = new short[outLen];
        var step = (double)fromRate / toRate;
        for (var i = 0; i < outLen; i++)
        {
            var pos = i * step;
            var i0 = (int)pos;
            var frac = pos - i0;
            var s0 = input[Math.Min(i0, input.Length - 1)];
            var s1 = input[Math.Min(i0 + 1, input.Length - 1)];
            output[i] = (short)Math.Clamp(s0 + (s1 - s0) * frac, short.MinValue, short.MaxValue);
        }
        return output;
    }
}
