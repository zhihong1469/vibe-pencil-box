// <copyright file="ChirpTrainSource.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using NAudio.Wave;
using PencilBox.Core.Dsp;

namespace PencilBox.Infrastructure.Audio;

/// <summary>
/// chirp 训练源（软件接收器 probe 发射半边）：16k 单声道无限训练列，
/// 每 <see cref="ChirpTemplate.PeriodSamples"/>（750ms）在列首放一个
/// <see cref="ChirpTemplate.Table"/>（120 点 1k→3k up-chirp，固件同源整数表）。
/// 消费端（WasapiOut+重采样/声道转换）按实时拉取；<see cref="SamplesEmitted"/>
/// 供发射网格对账（第 k 个 chirp 起于 k·PeriodSamples）。
/// </summary>
public sealed class ChirpTrainSource : ISampleProvider
{
    private long _pos;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(ChirpTemplate.SampleRate, 1);

    /// <summary>已累计产出的样点数（发射网格锚：chirp k 起于 k·PeriodSamples）</summary>
    public long SamplesEmitted => _pos;

    public int Read(float[] buffer, int offset, int count)
    {
        var start = _pos;
        for (var i = 0; i < count; i++)
        {
            var inPeriod = (start + i) % ChirpTemplate.PeriodSamples;
            buffer[offset + i] = inPeriod < ChirpTemplate.LengthSamples
                ? ChirpTemplate.Table[inPeriod] / 32768f
                : 0f;
        }
        _pos = start + count;
        return count;
    }
}
