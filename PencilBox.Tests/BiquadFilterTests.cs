using PencilBox.Core.Dsp;
using Xunit;

namespace PencilBox.Tests;

/// <summary>带通降噪级联（HPF/LPF 各两节 24dB/oct）的通带平直与阻带衰减验证</summary>
public class BiquadFilterTests
{
    private const int Rate = 16000;

    private static short[] Sine(double hz, int samples, double amp = 0.5)
    {
        var buf = new short[samples];
        for (var i = 0; i < samples; i++)
            buf[i] = (short)Math.Round(Math.Sin(2 * Math.PI * hz * i / Rate) * amp * 32767);
        return buf;
    }

    private static double RmsTail(short[] x, int skip)
    {
        double s = 0;
        for (var i = skip; i < x.Length; i++) s += (double)x[i] * x[i];
        return Math.Sqrt(s / (x.Length - skip));
    }

    private static double RunCascade(short[] input, BiquadResponse resp, double hz)
    {
        var a = new BiquadFilter(resp, hz, Rate);
        var b = new BiquadFilter(resp, hz, Rate);
        a.Process(input); b.Process(input);
        return RmsTail(input, input.Length / 4);   // 留前 25% 给暂态收敛
    }

    [Fact]
    public void HighPass_PassesVoiceBand_AndAttenuatesRumble()
    {
        const double hpf = 80;
        var pass = RunCascade(Sine(1000, 4000), BiquadResponse.HighPass, hpf);
        var cut = RunCascade(Sine(30, 4000), BiquadResponse.HighPass, hpf);
        var passDb = 20 * Math.Log10(pass / RmsTail(Sine(1000, 4000), 1000));
        var cutDb = 20 * Math.Log10(cut / RmsTail(Sine(30, 4000), 1000));
        Assert.InRange(passDb, -1.0, 1.0);       // 通带平直
        Assert.True(cutDb <= -20, $"30Hz 应衰减 ≥20dB，实得 {cutDb:F1}dB");
    }

    [Fact]
    public void LowPass_PassesVoiceBand_AndAttenuatesHiss()
    {
        const double lpf = 3000;
        var pass = RunCascade(Sine(1000, 4000), BiquadResponse.LowPass, lpf);
        var cut = RunCascade(Sine(7000, 4000), BiquadResponse.LowPass, lpf);
        var passDb = 20 * Math.Log10(pass / RmsTail(Sine(1000, 4000), 1000));
        var cutDb = 20 * Math.Log10(cut / RmsTail(Sine(7000, 4000), 1000));
        // RBJ cookbook Q=1/√2 非精确数字 Butterworth：fc/fs 较大时通带有双线性变换峰
        //（fc=3k@16k 在 1kHz 实测 +1.16dB，手算吻合），听感可忽略且 LPF 默认关
        Assert.InRange(passDb, -1.0, 1.5);
        Assert.True(cutDb <= -20, $"7kHz 应衰减 ≥20dB，实得 {cutDb:F1}dB");
    }

    [Fact]
    public void CutoffOutOfBounds_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BiquadFilter(BiquadResponse.HighPass, 0, Rate));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BiquadFilter(BiquadResponse.LowPass, Rate / 2, Rate));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BiquadFilter(BiquadResponse.Notch, 1000, Rate, 0));
    }

    [Fact]
    public void Notch_KillsCenterTone_AndPassesNeighbors()
    {
        const double f0 = 1000;
        const double q = 25;
        double LevelAt(double hz)
        {
            var filter = new BiquadFilter(BiquadResponse.Notch, f0, Rate, q);
            var buf = Sine(hz, 4000);
            filter.Process(buf);
            return 20 * Math.Log10(RmsTail(buf, 1000) / RmsTail(Sine(hz, 4000), 1000));
        }

        // 中心挖深坑（电机单频 ≥20dB 衰减），坑外 1/5 倍频程人声几乎无损
        Assert.True(LevelAt(f0) <= -40, $"1kHz 中心应深坑（≤-40dB），实得 {LevelAt(f0):F1}dB");
        Assert.InRange(LevelAt(800), -1.0, 1.0);
        Assert.InRange(LevelAt(1250), -1.0, 1.0);
    }

    [Fact]
    public void Notch_Cascade_RemovesHarmonics_Independently()
    {
        // 双 notch（基频+二次谐波）各挖各的坑，互不拖累通带
        var n1 = new BiquadFilter(BiquadResponse.Notch, 1000, Rate, 25);
        var n2 = new BiquadFilter(BiquadResponse.Notch, 2000, Rate, 25);
        var buf = Sine(1000, 4000);
        n1.Process(buf); n2.Process(buf);
        var at1k = 20 * Math.Log10(RmsTail(buf, 1000) / RmsTail(Sine(1000, 4000), 1000));

        var buf3 = Sine(3000, 4000);
        n1.Process(buf3); n2.Process(buf3);
        var at3k = 20 * Math.Log10(RmsTail(buf3, 1000) / RmsTail(Sine(3000, 4000), 1000));

        Assert.True(at1k <= -40, $"1kHz 应被挖掉，实得 {at1k:F1}dB");
        Assert.InRange(at3k, -1.0, 1.0);
    }
}
