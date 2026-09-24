using PencilBox.Core.Audio;
using PencilBox.Core.Dsp;

namespace PencilBox.Tests;

/// <summary>
/// 处理链纯逻辑单测：延迟环（SPSC/回看）/ 凑帧器 / 频移器 / 回声延迟对齐估计器
/// </summary>
public class DspChainTests
{
    // ===== ShortDelayRing =====

    [Fact]
    public void DelayRing_PeekAtLag_ReturnsLatestWindow()
    {
        var ring = new ShortDelayRing(64);
        var data = Enumerable.Range(0, 40).Select(i => (short)(i * 3)).ToArray();

        ring.Write(data);

        // 最新 5 个样本
        var dest = new short[5];
        Assert.True(ring.TryReadAt(dest, 0));
        Assert.Equal(new short[] { 35, 36, 37, 38, 39 }.Select(i => (short)(i * 3)).ToArray(), dest);

        // 往回 10 个样本结尾的 5 个窗口
        Assert.True(ring.TryReadAt(dest, 10));
        Assert.Equal(new short[] { 25, 26, 27, 28, 29 }.Select(i => (short)(i * 3)).ToArray(), dest);
    }

    [Fact]
    public void DelayRing_Wraparound_And_Underflow()
    {
        var ring = new ShortDelayRing(16);
        // 写入 20 个样本 → 环绕，最早 4 个被覆盖
        ring.Write(Enumerable.Range(0, 20).Select(i => (short)i).ToArray());

        var dest = new short[4];
        Assert.True(ring.TryReadAt(dest, 4));   // 最新 4 个之前的 4 个 = 12..15
        Assert.Equal(new short[] { 12, 13, 14, 15 }, dest);

        Assert.False(ring.TryReadAt(new short[13], 4));  // 13+4 > 容量16，最早样本已覆盖
        Assert.False(ring.TryReadAt(new short[4], 100)); // 历史不足
    }

    // ===== FrameStitcher =====

    [Fact]
    public void FrameStitcher_VariableChunks_ProduceExactFrames()
    {
        var stitcher = new FrameStitcher(8);
        var frame = new byte[8];
        var produced = new List<byte[]>();

        stitcher.Feed(new byte[] { 1, 2, 3 });          // 3B，不足一帧
        Assert.False(stitcher.TryReadFrame(frame));
        stitcher.Feed(new byte[] { 4, 5, 6, 7, 8 });    // +5B → 第一帧 1..8
        if (stitcher.TryReadFrame(frame)) produced.Add((byte[])frame.Clone());
        stitcher.Feed(new byte[] { 9, 10, 11, 12, 13 }); // +5B → 第二帧 9..16? 不：只剩 9..13 共 5B
        while (stitcher.TryReadFrame(frame)) produced.Add((byte[])frame.Clone());

        var first = Assert.Single(produced);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, first);
        Assert.False(stitcher.TryReadFrame(frame));   // 残料 5B 不足一帧
    }

    // ===== FreqShifter =====

    private static double EstimateFrequency(short[] signal, int sampleRate)
    {
        // 过零法估频
        var crossings = 0;
        for (var i = 1; i < signal.Length; i++)
            if (signal[i - 1] < 0 && signal[i] >= 0) crossings++;
        return (double)crossings * sampleRate / signal.Length;
    }

    private static short[] SineShorts(double freq, int sampleRate, int samples, double amp = 0.5)
    {
        var data = new short[samples];
        for (var i = 0; i < samples; i++)
            data[i] = (short)(amp * Math.Sin(2 * Math.PI * freq * i / sampleRate) * 32767);
        return data;
    }

    [Fact]
    public void FreqShifter_ShiftsSpectrumUp()
    {
        var shifter = new FreqShifter(16000, 5.0);
        const int n = 16000;
        var signal = SineShorts(440, 16000, n);

        // 分帧处理（模拟引擎 160 样本帧）
        for (var off = 0; off < n; off += 160)
        {
            var frame = signal.AsSpan(off, 160);
            shifter.Process(frame);
        }

        // 丢弃前 2000 样本（FIR 暂态 + 频率估计窗），稳态段应从 440Hz → ≈445Hz
        var steady = signal.Skip(2000).Take(12000).ToArray();
        var f = EstimateFrequency(steady, 16000);
        Assert.InRange(f, 443.5, 446.5);
    }

    // ===== EchoDelayEstimator =====

    [Fact]
    public void DelayEstimator_ConvergesToTrueDelay()
    {
        const int frame = 160;
        // 引擎约束：标称 ≥ 2×搜索半径（此处 320），真延迟须落在标称±搜索内（250 ∈ 320±160）
        const int nominal = 320, trueDelay = 250;
        var est = new EchoDelayEstimator(frame, 16000, searchMs: 10, updateEveryFrames: 10);
        var rng = new Random(42);

        // 远端参考 = 伪随机噪声；mic = 延迟 trueDelay 的衰减回声 + 独立小噪声
        var refHistory = new List<short>();
        for (var step = 0; step < 200; step++)
        {
            var reference = new short[frame];
            for (var i = 0; i < frame; i++) reference[i] = (short)rng.Next(-8000, 8000);
            refHistory.AddRange(reference);

            var mic = new short[frame];
            for (var i = 0; i < frame; i++)
            {
                var src = refHistory.Count - frame + i - trueDelay;
                var echo = src >= 0 ? refHistory[src] * 0.6 : 0;
                mic[i] = (short)(echo + rng.Next(-300, 300));
            }

            // 完全复刻引擎的宽窗取数：backOffset_wide = 标称 + Offset - Search
            var backWide = nominal + est.OffsetSamples - est.SearchSamples;
            var wide = new short[frame + 2 * est.SearchSamples];
            for (var i = 0; i < wide.Length; i++)
            {
                var pos = refHistory.Count - (wide.Length - i) - backWide;
                wide[i] = pos >= 0 && pos < refHistory.Count ? refHistory[pos] : (short)0;
            }

            est.Update(mic, wide);
        }

        // 收敛后 Offset ≈ trueDelay - 标称 = -70（参考取数位置 = 标称 + Offset = 真实回声延迟）
        Assert.InRange(est.OffsetSamples, -90, -50);
        Assert.True(est.LastPeakQuality > 0.5f, $"相关峰质量 {est.LastPeakQuality:F2} 过低");
    }
}
