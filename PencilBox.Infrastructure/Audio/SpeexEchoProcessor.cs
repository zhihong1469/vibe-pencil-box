// <copyright file="SpeexEchoProcessor.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Services;
using SpeexDSPSharp.Core;

namespace PencilBox.Infrastructure.Audio;

/// <summary>
/// SpeexDSP 回声消除 + 残余抑制（教科书链：自适应频域 MDF → NLP/噪声门）。
/// 16k 单声道 PCM16 定帧处理；线程约定：仅采集线程调用 <see cref="Process"/>。
/// 本类持有非托管状态，必须 Dispose。
/// </summary>
public sealed class SpeexEchoProcessor : IEchoProcessor, IDisposable
{
    private readonly SpeexDSPEchoCanceler _echo;
    private readonly SpeexDSPPreprocessor _prep;
    private readonly int _frameSamples;

    public SpeexEchoProcessor(int frameSamples, int tailSamples, int sampleRate)
    {
        _frameSamples = frameSamples;
        _echo = new SpeexDSPEchoCanceler(frameSamples, tailSamples, null);
        _prep = new SpeexDSPPreprocessor(frameSamples, sampleRate, null);

        // 前置处理：去噪开（兼残余回声抑制），AGC/VAD 关（增益策略在上层）
        int one = 1, zero = 0;
        _prep.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_DENOISE, ref one);
        _prep.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_AGC, ref zero);
        _prep.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_VAD, ref zero);
    }

    public int FrameSamples => _frameSamples;

    public void Process(short[] mic, short[] reference, short[] output)
    {
        _echo.EchoCancel(mic, reference, output);
        _prep.Run(output);   // 残余回声 + 噪声抑制（教科书顺序：AEC → NLP/NS）
    }

    public void ProcessReferenceOnly(short[] frame) => _prep.Run(frame);

    public void SetEchoSuppress(int db, int activeDb)
    {
        var suppress = -Math.Clamp(db, 5, 90);
        var active = -Math.Clamp(activeDb, 3, 90);
        _prep.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_ECHO_SUPPRESS, ref suppress);
        _prep.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_ECHO_SUPPRESS_ACTIVE, ref active);
    }

    public void SetNoiseSuppress(int db)
    {
        var noise = -Math.Clamp(db, 0, 60);
        _prep.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_NOISE_SUPPRESS, ref noise);
    }

    public void Reset()
    {
        _echo.Reset();
    }

    public void Dispose()
    {
        _echo.Dispose();
        _prep.Dispose();
    }
}
