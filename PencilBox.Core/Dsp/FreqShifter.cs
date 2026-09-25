// <copyright file="FreqShifter.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Dsp;

/// <summary>
/// 频移器（啸叫抑制 AFC，PA 移频法）：把信号频谱整体平移几 Hz，破坏闭环啸叫所需的相位相干。
/// 实现 = Hamming 窗截断 Hilbert FIR 构造解析信号（I=延迟对齐的实部，Q=希尔伯特变换），
/// 乘复正弦取实部 → 上移 shiftHz。因果零前看，附加群延迟 (Taps-1)/2 个样本。
/// 代价：轻微"合唱感"音质损伤，故默认关闭、按需开启。
/// </summary>
public sealed class FreqShifter
{
    private readonly float[] _hilbert;   // h[n]，奇数长，中心项为 0
    private readonly float[] _history;   // 输入延迟线（样本）
    private readonly int _center;        // (Taps-1)/2，I 路对齐延迟
    private readonly double _omega;      // 2π·shift/rate，每样本旋转角
    private double _phase;
    private int _histPos;

    public FreqShifter(int sampleRate, double shiftHz, int taps = 33)
    {
        if (sampleRate < 1) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (taps < 3 || taps % 2 == 0) throw new ArgumentOutOfRangeException(nameof(taps), "Hilbert FIR 必须为奇数长");
        SampleRate = sampleRate;
        ShiftHz = shiftHz;
        _center = (taps - 1) / 2;
        _omega = 2 * Math.PI * shiftHz / sampleRate;

        // 理想希尔伯特变换器 + Hamming 窗：h[n] = (1 - cos(π(n-M/2))) / (π(n-M/2))
        _hilbert = new float[taps];
        var m = taps - 1;
        for (var n = 0; n < taps; n++)
        {
            if (n == _center) continue;
            var k = n - _center;
            var w = 0.54 - 0.46 * Math.Cos(2 * Math.PI * n / m);   // Hamming
            _hilbert[n] = (float)((1 - Math.Cos(Math.PI * k)) / (Math.PI * k) * w);
        }

        _history = new float[taps];
    }

    public int SampleRate { get; }
    public double ShiftHz { get; }
    /// <summary>群延迟（样本），供延迟预算记账</summary>
    public int GroupDelaySamples => _center;

    /// <summary>原地处理交错单声道帧（short 域进出，内部 float 精度）</summary>
    public void Process(Span<short> frame)
    {
        foreach (ref var s in frame)
        {
            var x = s / 32768f;

            _history[_histPos] = x;
            // Q 路：Hilbert FIR 全卷积；I 路：取 _center 前的样本（线性相位对齐）
            float q = 0;
            var idx = _histPos;
            for (var k = 0; k < _hilbert.Length; k++)
            {
                q += _hilbert[k] * _history[idx];
                idx = idx == 0 ? _hilbert.Length - 1 : idx - 1;
            }
            var i = _history[(_histPos - _center + _hilbert.Length) % _hilbert.Length];
            _histPos = (_histPos + 1) % _hilbert.Length;

            _phase += _omega;
            if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
            var y = i * (float)Math.Cos(_phase) - q * (float)Math.Sin(_phase);

            s = (short)MathF.Round(Math.Clamp(y, -1f, 1f) * 32767f);
        }
    }

    /// <summary>清状态（开关重开时）</summary>
    public void Reset()
    {
        Array.Clear(_history);
        _phase = 0;
        _histPos = 0;
    }
}
