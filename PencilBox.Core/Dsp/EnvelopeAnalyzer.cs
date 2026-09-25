// <copyright file="EnvelopeAnalyzer.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Dsp;

/// <summary>一个信封块的电平（dBFS，-90 表示纯静音）</summary>
public sealed record EnvelopeBlock(int StartSample, double RmsDbfs, double PeakDbfs);

/// <summary>
/// WAV 包络分析结果：整体电平、逐块信封、以及批次 1 验收要读的三个数——
/// 首个出声时刻（起播阈值代理）、尾音长度（D6 停止清缓冲验证）、语音总时长。
/// </summary>
public sealed record EnvelopeReport(
    double PeakDbfs,
    double RmsDbfs,
    int SampleRate,
    int BlockMs,
    int ThresholdDbfs,
    IReadOnlyList<EnvelopeBlock> Blocks,
    int SpeechBlocks,
    double SpeechSeconds,
    double? FirstSoundMs,
    double? TailMs,
    double TotalSeconds);

/// <summary>
/// 离线 WAV 信封分析（纯数学，输入 16-bit 单声道样本）。
/// 判"有声"用块 RMS 过阈值（与 2026-09-24 UAC 排障用的 PowerShell 口径一致）。
/// </summary>
public static class EnvelopeAnalyzer
{
    public const double SilenceDbfs = -90;

    /// <param name="samples">16-bit 单声道样本</param>
    /// <param name="sampleRate">采样率</param>
    /// <param name="blockMs">信封块长（默认 100ms）</param>
    /// <param name="thresholdDbfs">块 RMS 有声判阈（默认 -35dBFS）</param>
    public static EnvelopeReport Analyze(short[] samples, int sampleRate, int blockMs = 100, int thresholdDbfs = -35)
    {
        if (samples.Length < 1) throw new ArgumentOutOfRangeException(nameof(samples));
        if (blockMs < 1) throw new ArgumentOutOfRangeException(nameof(blockMs));

        var blockSamples = Math.Max(1, sampleRate * blockMs / 1000);
        var peak = 0;
        double sumSq = 0;
        var blocks = new List<EnvelopeBlock>();
        var speechBlocks = 0;
        double? firstSoundStart = null;
        double? lastSoundEnd = null;

        for (var start = 0; start < samples.Length; start += blockSamples)
        {
            var len = Math.Min(blockSamples, samples.Length - start);
            double blockSq = 0;
            var blockPeak = 0;
            for (var i = start; i < start + len; i++)
            {
                var v = samples[i];
                var a = v < 0 ? -v : v;
                if (a > blockPeak) blockPeak = a;
                blockSq += (double)v * v;
            }
            var rms = Math.Sqrt(blockSq / len);
            var rmsDb = Dbfs(rms);
            var peakDb = Dbfs(blockPeak);
            blocks.Add(new EnvelopeBlock(start, rmsDb, peakDb));

            if (blockPeak > peak) peak = blockPeak;
            for (var i = start; i < start + len; i++)
            {
                var v = (double)samples[i];
                sumSq += v * v;
            }

            if (rmsDb > thresholdDbfs)
            {
                speechBlocks++;
                firstSoundStart ??= start * 1000.0 / sampleRate;
                lastSoundEnd = (start + len) * 1000.0 / sampleRate;
            }
        }

        var totalSec = samples.Length / (double)sampleRate;
        var overallRms = Math.Sqrt(sumSq / samples.Length);
        var tailMs = lastSoundEnd is { } end ? Math.Max(0, totalSec * 1000 - end) : (double?)null;

        return new EnvelopeReport(
            Dbfs(peak),
            Dbfs(overallRms),
            sampleRate,
            blockMs,
            thresholdDbfs,
            blocks,
            speechBlocks,
            speechBlocks * blockMs / 1000.0,
            firstSoundStart,
            tailMs,
            totalSec);

        static double Dbfs(double v) => v <= 0 ? SilenceDbfs : Math.Max(SilenceDbfs, 20 * Math.Log10(v / 32768.0));
    }
}
