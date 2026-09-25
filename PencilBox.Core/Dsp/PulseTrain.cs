// <copyright file="PulseTrain.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Dsp;

/// <summary>
/// 测量用脉冲串生成（端到端延迟标定，box/03 方案 C）。
/// 单脉冲 = 线性扫频 + 指数衰减包络：互相关峰锐利、对房间反射与小扬声器频响不敏感；
/// 脉冲间隔须大于链路最大预期延迟 + 混响尾，避免前后相关峰粘连。
/// </summary>
public static class PulseTrain
{
    /// <summary>默认扫频端点（引擎播放与 CLI 检测模板共用，防两处漂移）</summary>
    public const double DefaultF0Hz = 1000;
    public const double DefaultF1Hz = 8000;

    /// <summary>
    /// 生成单个扫频脉冲（16-bit PCM）。
    /// </summary>
    /// <param name="sampleRate">采样率</param>
    /// <param name="durationMs">脉冲时长（box/03 建议 10~20ms）</param>
    /// <param name="f0Hz">起始频率（建议 100~1000）</param>
    /// <param name="f1Hz">结束频率（≤sampleRate/2 的一半以内）</param>
    /// <param name="amplitude">峰值幅度 0~1</param>
    public static short[] GenerateChirp(int sampleRate, int durationMs, double f0Hz = DefaultF0Hz, double f1Hz = DefaultF1Hz, double amplitude = 0.8)
    {
        if (sampleRate < 8000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (durationMs < 1) throw new ArgumentOutOfRangeException(nameof(durationMs));
        if (f1Hz > sampleRate / 2.0) throw new ArgumentException($"f1({f1Hz}Hz) 超过奈奎斯特({sampleRate / 2.0}Hz)");

        var n = sampleRate * durationMs / 1000;
        var buf = new short[n];
        var durSec = n / (double)sampleRate;
        var phase = 0.0;
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)sampleRate;
            var frac = t / durSec;
            var f = f0Hz + (f1Hz - f0Hz) * frac;
            phase += 2 * Math.PI * f / sampleRate;
            var env = Math.Exp(-3.0 * frac);              // 指数衰减，尾部收敛避免相关峰拖尾
            buf[i] = (short)Math.Clamp(Math.Sin(phase) * env * amplitude * 32767, short.MinValue, short.MaxValue);
        }
        return buf;
    }

    /// <summary>
    /// 把单脉冲铺成脉冲串（等间隔、其余置零）。
    /// 返回整段信号与每个脉冲在信号中的起始样本位置。
    /// </summary>
    public static (short[] Signal, int[] PulsePositions) Build(
        short[] pulse, int sampleRate, int pulseCount, int intervalMs)
    {
        if (pulse.Length < 1) throw new ArgumentOutOfRangeException(nameof(pulse));
        if (pulseCount < 1) throw new ArgumentOutOfRangeException(nameof(pulseCount));
        var intervalSamples = sampleRate * intervalMs / 1000;
        if (intervalSamples <= pulse.Length)
            throw new ArgumentException($"间隔({intervalMs}ms)必须大于脉冲时长({pulse.Length * 1000.0 / sampleRate:F1}ms)+保护带");

        var total = (pulseCount - 1) * intervalSamples + pulse.Length;
        var signal = new short[total];
        var positions = new int[pulseCount];
        for (var k = 0; k < pulseCount; k++)
        {
            positions[k] = k * intervalSamples;
            Array.Copy(pulse, 0, signal, positions[k], pulse.Length);
        }
        return (signal, positions);
    }
}
