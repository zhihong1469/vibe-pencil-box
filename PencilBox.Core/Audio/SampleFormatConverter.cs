// <copyright file="SampleFormatConverter.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Audio;

/// <summary>
/// 流式采样格式转换（扩音直通用）：采集格式 → 渲染端点混音格式。
/// 零前视：一个输出样本只依赖已到达的输入（滞后 1 个输入帧）；跨块状态保持。
/// 输入必须整帧对齐（WASAPI 回调保证；不足整帧的尾字节被截去）。
/// 线性插值重采样 + 声道复制/下混 + PCM16/float32 互转。语音 PA 场景线性插值质量足够；
/// 后续升级多相滤波不改变接口。
/// </summary>
public sealed class SampleFormatConverter
{
    private readonly int _inRate;
    private readonly int _inChannels;
    private readonly bool _inIsFloat;
    private readonly int _inFrameBytes;

    private readonly int _outRate;
    private readonly int _outChannels;
    private readonly bool _outIsFloat;
    private readonly int _outFrameBytes;

    private readonly double _step;   // 每个输出样本推进的输入帧数 = inRate / outRate
    private readonly float[] _last;  // 输出相位所在区间的左帧 f[floor(t)]
    private readonly float[] _cur;   // 右帧 f[floor(t)+1]（零前视的"未来"帧）
    private bool _hasCur;            // _cur 是否已初始化（首块特判）
    private double _phase;           // ∈[0,1)：_last → _cur 的位置

    public SampleFormatConverter(
        int inRate, int inBitsPerSample, bool inIsFloat, int inChannels,
        int outRate, int outBitsPerSample, bool outIsFloat, int outChannels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(inRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(outRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(inChannels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(outChannels, 1);
        if (!inIsFloat && inBitsPerSample != 16) throw new NotSupportedException("输入仅支持 PCM16 或 float32");
        if (outIsFloat && outBitsPerSample != 32) throw new NotSupportedException("float 输出仅支持 32 位");
        if (!outIsFloat && outBitsPerSample != 16) throw new NotSupportedException("输出仅支持 PCM16 或 float32");

        _inRate = inRate;
        _inChannels = inChannels;
        _inIsFloat = inIsFloat;
        _inFrameBytes = inChannels * (inBitsPerSample / 8);
        _outRate = outRate;
        _outChannels = outChannels;
        _outIsFloat = outIsFloat;
        _outFrameBytes = outChannels * (outBitsPerSample / 8);
        _step = (double)inRate / outRate;
        _last = new float[inChannels];
        _cur = new float[inChannels];
    }

    /// <summary>输出格式与输入完全一致时可走直拷</summary>
    public bool IsPassthrough =>
        _inRate == _outRate && _inChannels == _outChannels && _inIsFloat == _outIsFloat;

    /// <summary>
    /// 消费整段输入，写出转换后字节，返回输出字节数（输入总是被完整消费）。
    /// output 容量须 ≥ <see cref="MaxOutputBytesFor"/>，否则抛 ArgumentException。
    /// </summary>
    public int Convert(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (IsPassthrough)
        {
            if (output.Length < input.Length) throw new ArgumentException("output 容量不足", nameof(output));
            input.CopyTo(output);
            return input.Length;
        }
        if (output.Length < MaxOutputBytesFor(input.Length))
            throw new ArgumentException("output 容量不足（须 ≥ MaxOutputBytesFor）", nameof(output));

        var inFrames = input.Length / _inFrameBytes;
        var maxOutFrames = output.Length / _outFrameBytes;
        var outFrames = 0;
        var idx = 0;

        if (!_hasCur)
        {
            if (inFrames == 0) return 0;
            ReadFrame(input[.._inFrameBytes], _cur);
            _cur.CopyTo(_last, 0);          // 首块 _last = _cur = f0，避免开头空拍
            _hasCur = true;
            idx = 1;
        }

        while (outFrames < maxOutFrames)
        {
            if (_phase >= 1.0)
            {
                _cur.CopyTo(_last, 0);      // _last = _cur（块耗尽时重复赋值幂等，跨块正确续接）
                if (idx < inFrames)
                {
                    ReadFrame(input.Slice(idx * _inFrameBytes, _inFrameBytes), _cur);
                    idx++;
                    _phase -= 1.0;
                    continue;
                }
                return outFrames * _outFrameBytes;   // 本块耗尽：_cur 保持为最后一个输入帧
            }

            EmitFrame(output.Slice(outFrames * _outFrameBytes, _outFrameBytes));
            outFrames++;
            _phase += _step;
        }

        return outFrames * _outFrameBytes;
    }

    /// <summary>给定输入字节数，保证足够的输出容量（含相位边界余量）</summary>
    public int MaxOutputBytesFor(int inputBytes)
    {
        var inFrames = inputBytes / _inFrameBytes;
        var outFrames = (int)Math.Ceiling(inFrames * (double)_outRate / _inRate) + 2;
        return outFrames * _outFrameBytes;
    }

    private void ReadFrame(ReadOnlySpan<byte> src, Span<float> dst)
    {
        if (_inIsFloat)
        {
            for (var c = 0; c < _inChannels; c++)
                dst[c] = BitConverter.ToSingle(src.Slice(c * 4, 4));
        }
        else
        {
            for (var c = 0; c < _inChannels; c++)
                dst[c] = BitConverter.ToInt16(src.Slice(c * 2, 2)) / 32768f;
        }
    }

    private void EmitFrame(Span<byte> dst)
    {
        if (_outIsFloat)
        {
            for (var c = 0; c < _outChannels; c++)
                BitConverter.GetBytes(SampleFor(c)).CopyTo(dst.Slice(c * 4, 4));
        }
        else
        {
            for (var c = 0; c < _outChannels; c++)
            {
                var v = (short)Math.Clamp(SampleFor(c) * 32767f, short.MinValue, short.MaxValue);
                BitConverter.GetBytes(v).CopyTo(dst.Slice(c * 2, 2));
            }
        }
    }

    private float SampleFor(int outChannel)
    {
        if (_outChannels < _inChannels)
        {
            // 下混：输出声道取所有输入声道均值
            float last = 0f, cur = 0f;
            for (var i = 0; i < _inChannels; i++) { last += _last[i]; cur += _cur[i]; }
            last /= _inChannels;
            cur /= _inChannels;
            return last + (cur - last) * (float)_phase;
        }
        var k = Math.Min(outChannel, _inChannels - 1);   // 上混：复制最后一个输入声道
        return _last[k] + (_cur[k] - _last[k]) * (float)_phase;
    }
}
