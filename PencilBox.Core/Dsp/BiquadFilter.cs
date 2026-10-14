// <copyright file="BiquadFilter.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Dsp;

/// <summary>滤波器响应类型</summary>
public enum BiquadResponse
{
    /// <summary>高通：切机械抖动/低频传导（带通降噪的下边）</summary>
    HighPass,
    /// <summary>低通：切高频嘶声/超声残余（带通降噪的上边；16k 处理时 8k 以上已被采样率天然滤除）</summary>
    LowPass,
}

/// <summary>
/// RBJ cookbook 双二阶级联节（Q=1/√2 Butterworth）。单节 12dB/oct，带通降噪用两节 = 24dB/oct。
/// short 帧内原地处理，状态跨帧延续；<see cref="Design"/> 重设系数并清零状态。
/// </summary>
public sealed class BiquadFilter
{
    private float _b0, _b1, _b2, _a1, _a2;
    private float _x1, _x2, _y1, _y2;

    public BiquadResponse Response { get; private set; }
    public double CutoffHz { get; private set; }
    public int SampleRate { get; private set; }

    public BiquadFilter(BiquadResponse response, double cutoffHz, int sampleRate)
        => Design(response, cutoffHz, sampleRate);

    public void Design(BiquadResponse response, double cutoffHz, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cutoffHz, 1.0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cutoffHz, sampleRate / 2.0 * 0.99);
        Response = response;
        CutoffHz = cutoffHz;
        SampleRate = sampleRate;

        var w0 = 2 * Math.PI * cutoffHz / sampleRate;
        var c = Math.Cos(w0);
        var s = Math.Sin(w0);
        var alpha = s / (2 * Math.Sqrt(2));              // Q = 1/√2
        float b0, b1, b2, a0, a1, a2;
        if (response == BiquadResponse.HighPass)
        {
            b0 = (float)((1 + c) / 2); b1 = (float)(-(1 + c)); b2 = (float)((1 + c) / 2);
        }
        else
        {
            b0 = (float)((1 - c) / 2); b1 = (float)(1 - c); b2 = (float)((1 - c) / 2);
        }
        a0 = (float)(1 + alpha); a1 = (float)(-2 * c); a2 = (float)(1 - alpha);

        _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0;
        _a1 = a1 / a0; _a2 = a2 / a0;
        _x1 = _x2 = _y1 = _y2 = 0;
    }

    /// <summary>单节帧内原地处理（一节 12dB/oct；带通降噪级联两节）</summary>
    public void Process(short[] frame)
    {
        for (var i = 0; i < frame.Length; i++)
        {
            var x = frame[i] / 32768f;
            var y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
            frame[i] = (short)MathF.Round(Math.Clamp(y, -1f, 1f) * 32767f);
        }
    }
}
