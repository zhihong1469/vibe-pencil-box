using PencilBox.Core.Audio;

namespace PencilBox.Tests;

/// <summary>
/// 零前看峰值限制器单测：天花板约束、透明度、释放平滑、声道独立
/// </summary>
public class SoftLimiterTests
{
    private static float[] Sine(float amplitude, float freqHz, int sampleRate, int frames, int channels = 1)
    {
        var data = new float[frames * channels];
        for (var i = 0; i < frames; i++)
        {
            var v = amplitude * MathF.Sin(2 * MathF.PI * freqHz * i / sampleRate);
            for (var c = 0; c < channels; c++) data[i * channels + c] = v;
        }
        return data;
    }

    private static float Peak(IReadOnlyList<float> data) => data.Max(MathF.Abs);

    [Fact]
    public void Transparent_BelowCeiling()
    {
        var lim = new SoftLimiter(1, 48000);
        var data = Sine(0.5f, 440f, 48000, 480);
        var expected = (float[])data.Clone();

        lim.Process(data);

        Assert.Equal(expected, data);
        Assert.Equal(1f, lim.CurrentGain);
    }

    [Fact]
    public void HardLimit_Gain24_NeverExceedsCeiling()
    {
        var lim = new SoftLimiter(1, 48000);
        // 0.05 峰值 × 24 增益 = 1.2 理论峰值 → 必须压回天花板内
        var data = Sine(0.05f, 440f, 48000, 4800);
        for (var i = 0; i < data.Length; i++) data[i] *= 24f;

        lim.Process(data);

        Assert.True(Peak(data) <= SoftLimiter.Ceiling + 1e-3f,
            $"峰值 {Peak(data):F4} 超过天花板 {SoftLimiter.Ceiling}");
        // 限制器不是静音器：响段仍应接近天花板
        Assert.True(Peak(data) >= 0.85f, $"压限后峰值 {Peak(data):F4} 过低，损失过大");
        Assert.True(lim.CurrentGain < 1f, "触顶后 CurrentGain 应小于 1");
    }

    [Fact]
    public void NoHarshDiscontinuity_AfterLimiting()
    {
        var lim = new SoftLimiter(1, 48000);
        var data = Sine(0.05f, 480f, 48000, 4800);
        for (var i = 0; i < data.Length; i++) data[i] *= 24f;

        lim.Process(data);

        // 相邻样本跳变有界（硬削波会产生陡峭沿）：480Hz@48k 正常斜率 ≈ 0.063
        float maxDelta = 0;
        for (var i = 1; i < data.Length; i++)
            maxDelta = MathF.Max(maxDelta, MathF.Abs(data[i] - data[i - 1]));
        Assert.True(maxDelta < 0.09f, $"最大样本跳变 {maxDelta:F4}，疑似硬削波");
    }

    [Fact]
    public void Recovers_AfterLoudBurstEnds()
    {
        var lim = new SoftLimiter(1, 48000);
        var loud = Sine(0.9f, 440f, 48000, 480);
        for (var i = 0; i < loud.Length; i++) loud[i] *= 24f;
        lim.Process(loud);
        Assert.True(lim.CurrentGain < 1f);

        // 释放时间常数 80ms @48k：满幅×24 的包络（≈21.6）衰减到天花板下约需 250ms，
        // 取 400ms 验证已恢复透明
        var quiet = Sine(0.01f, 440f, 48000, 480 * 40);
        lim.Process(quiet);
        Assert.True(lim.CurrentGain > 0.99f, $"释放后 CurrentGain={lim.CurrentGain:F4}，未恢复透明");
    }

    [Fact]
    public void Channels_Independent()
    {
        var lim = new SoftLimiter(2, 48000);
        // 左声道超响、右声道安静：右声道必须原样通过
        var frames = 480;
        var data = new float[frames * 2];
        var right = Sine(0.1f, 440f, 48000, frames);
        for (var i = 0; i < frames; i++)
        {
            data[i * 2] = 1.0f;          // 左：满幅方波，必然触顶
            data[i * 2 + 1] = right[i];  // 右：安静
        }

        lim.Process(data);

        for (var i = 0; i < frames; i++)
            Assert.Equal(right[i], data[i * 2 + 1], 4);
    }

    [Fact]
    public void ShortRoundtrip_EngineStyle_NoClipping()
    {
        // 引擎 PCM16 路径同款流程：float 往返后不得溢出 short
        var lim = new SoftLimiter(1, 48000);
        var volume = 24f;
        short[] samples = new short[480];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (short)(MathF.Sin(2 * MathF.PI * 440f * i / 48000) * 2000); // 峰值 2000×24=48000 → 溢出

        var f = samples.Select(s => s / 32768f * volume).ToArray();
        lim.Process(f);
        for (var i = 0; i < samples.Length; i++)
        {
            var back = (short)MathF.Round(Math.Clamp(f[i], -1f, 1f) * 32767f);
            Assert.True(Math.Abs(back) <= 31129, $"样本 {back} 超出天花板对应幅度"); // 0.95×32767
        }
    }
}
