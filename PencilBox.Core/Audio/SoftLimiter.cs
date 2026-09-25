// <copyright file="SoftLimiter.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Audio;

/// <summary>
/// 零前看峰值限制器（防破音）：把输出峰值压在硬天花板之下，供软件增益 &gt;1 时保住扬声器前最后一道关。
/// 压限立即生效（attack=0，逐样本看包络），释放按指数回落恢复透明；每声道独立包络。
/// 处理 float32 交错 PCM；样本数不必按帧对齐。
/// </summary>
public sealed class SoftLimiter
{
    /// <summary>输出峰值天花板（≈ -0.45 dBFS）。恒定上限，任何增益下输出不会超过它。</summary>
    public const float Ceiling = 0.95f;

    private readonly float[] _env;
    private readonly float _releaseCoef;
    private float _currentGain = 1f;

    /// <param name="channels">声道数（≥1）</param>
    /// <param name="sampleRate">采样率（Hz，&gt;0）</param>
    /// <param name="releaseMs">释放时间常数（ms），越长越平滑、跟踪越钝</param>
    public SoftLimiter(int channels, int sampleRate, float releaseMs = 80f)
    {
        if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
        if (sampleRate < 1) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        Channels = channels;
        _env = new float[channels];
        var tau = MathF.Max(1f, releaseMs / 1000f * sampleRate);
        _releaseCoef = MathF.Exp(-1f / tau);
    }

    public int Channels { get; }

    /// <summary>最近一样本的限制器增益（0~1；1 = 未触发，瞬时值）。仅供统计观测，读侧无需加锁（float 原子读写）。</summary>
    public float CurrentGain => _currentGain;

    /// <summary>原地处理交错 float32 PCM。任何输入下输出样本绝对值 ≤ <see cref="Ceiling"/>。</summary>
    public void Process(Span<float> interleaved)
    {
        var ch = Channels;
        for (var i = 0; i < interleaved.Length; i++)
        {
            var c = i % ch;
            var x = interleaved[i];
            var ax = MathF.Abs(x);

            // 包络：上冲瞬时跟随（attack=0），下落指数释放
            // 必须取 max(ax, e_prev×coef)：若在信号缓降段直接取 e_prev×coef，
            // 包络会掉到当前幅度之下，输出将突破天花板
            var e = _env[c] * _releaseCoef;
            if (ax > e) e = ax;
            _env[c] = e;

            var g = e > Ceiling ? Ceiling / e : 1f;
            _currentGain = g;
            interleaved[i] = x * g;
        }
    }
}
