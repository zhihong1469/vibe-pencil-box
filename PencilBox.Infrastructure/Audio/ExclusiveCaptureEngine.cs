// <copyright file="ExclusiveCaptureEngine.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PencilBox.Infrastructure.Audio;

/// <summary>独占录音结果（含实报格式——t7 B-1 验收要求"文件落地且报告实报格式"）</summary>
public sealed record ExclusiveCaptureResult(
    string Path,
    string DeviceName,
    WaveFormat Format,
    long Samples,
    double Seconds,
    double WallSeconds,
    int Events,
    int Packets);

/// <summary>
/// 独占模式采集（t7 B-1 半天件）：NAudio 的 WasapiCapture 仅共享模式，
/// 本引擎自持 AudioClient.Initialize(EXCLUSIVE, EVENTCALLBACK) 采集循环。
/// 格式协商：候选表逐个 IsFormatSupported(EXCLUSIVE)（UAC 1.0 未必原生给 16k/mono，
/// 全不支持则报实情而非硬猜）；缓冲周期取候选 {10ms, 20ms}（采集默认周期业界恒 10ms，
/// NAudio 包装层不暴露 GetDevicePeriod），遇 0x88890019 非对齐按 10ms 基准对齐重试。
/// 事件驱动读循环：SetEvent → GetNextPacketSize/GetBuffer/ReleaseBuffer → WAV 落盘逐包 flush。
/// </summary>
public static class ExclusiveCaptureEngine
{
    /// <summary>独占候选格式（16k 优先=t7 治理基线口径；全不支持时如实报错）</summary>
    public static readonly WaveFormat[] CandidateFormats =
    {
        new(16000, 16, 1),
        new(16000, 16, 2),
        new(48000, 16, 1),
        new(48000, 16, 2),
    };

    private const long Period10Ms = 100_000;    // hns；采集端默认设备周期
    private const long Period20Ms = 200_000;

    /// <summary>
    /// 设备解析（纯函数可测）：spec 为空取默认采集端点；否则按 EndpointID 包含 → FriendlyName 包含匹配。
    /// </summary>
    public static T? MatchDevice<T>(IReadOnlyList<T> endpoints, Func<T, (string Id, string Name)> key, string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return default;
        return endpoints.FirstOrDefault(d =>
        {
            var (id, name) = key(d);
            return (!string.IsNullOrEmpty(id) && id.Contains(spec, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(name) && name.Contains(spec, StringComparison.OrdinalIgnoreCase));
        });
    }

    public static ExclusiveCaptureResult Record(string? deviceId, int seconds, string outputPath,
        CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var enumerator = new MMDeviceEnumerator();
        MMDevice device;
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        }
        else
        {
            var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
            device = MatchDevice(endpoints, d => (d.ID, d.FriendlyName), deviceId)
                     ?? throw new ArgumentException(
                         $"未找到匹配的采集端点：{deviceId}（用 audio devices 查 ID/名称）");
        }

        var chosen = CandidateFormats.FirstOrDefault(f =>
        {
            using var probe = device.AudioClient;
            return probe.IsFormatSupported(AudioClientShareMode.Exclusive, f);
        }) ?? throw new NotSupportedException(
            $"设备「{device.FriendlyName}」独占模式不支持候选格式（16k/48k mono|stereo s16）——" +
            "独占需设备原生暴露该格式，UAC 1.0 若只给别的组合需扩候选表");

        using var audioClient = device.AudioClient;
        InitializeExclusive(audioClient, chosen);

        using var ev = new EventWaitHandle(false, EventResetMode.AutoReset);
        audioClient.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());

        var capture = audioClient.AudioCaptureClient;
        var buffer = new byte[chosen.SampleRate / 5 * chosen.BlockAlign];   // 200ms 容量上界
        using var writer = new WaveFileWriter(outputPath, chosen);
        audioClient.Start();
        var sw = Stopwatch.StartNew();
        try
        {
            long samples = 0;
            var events = 0;
            var packets = 0;
            while (sw.Elapsed.TotalSeconds < seconds && !ct.IsCancellationRequested)
            {
                if (!ev.WaitOne(200)) continue;
                events++;
                while (capture.GetNextPacketSize() > 0)
                {
                    var ptr = capture.GetBuffer(out var frames, out _);
                    var bytes = frames * chosen.BlockAlign;
                    if (bytes > 0)
                    {
                        if (bytes > buffer.Length) Array.Resize(ref buffer, bytes);
                        System.Runtime.InteropServices.Marshal.Copy(ptr, buffer, 0, bytes);
                        writer.Write(buffer, 0, bytes);
                        samples += frames;
                        packets++;
                    }
                    capture.ReleaseBuffer(frames);
                }
                writer.Flush();
            }
            var wall = sw.Elapsed.TotalSeconds;
            writer.Flush();
            return new ExclusiveCaptureResult(outputPath, device.FriendlyName, chosen, samples,
                samples / (double)chosen.SampleRate, wall, events, packets);
        }
        finally
        {
            try { audioClient.Stop(); } catch { /* 已停则忽略 */ }
        }
    }

    /// <summary>
    /// 独占+事件初始化（2026-11-05 起 public：<see cref="ExclusiveCapturePump"/> 流式泵复用同一配方）：
    /// periodicity 必须等于 bufferDuration；0x88890019=非对齐，按 10ms 基准对齐重试。
    /// 设备被占/格式被拒抛 COMException，由调用方归因。
    /// </summary>
    public static void InitializeExclusive(AudioClient client, WaveFormat format)
    {
        const int audclntEBufferSizeNotAligned = unchecked((int)0x88890019);
        foreach (var period in new[] { Period10Ms, Period20Ms })
        {
            try
            {
                client.Initialize(AudioClientShareMode.Exclusive, AudioClientStreamFlags.EventCallback,
                    period, period, format, Guid.Empty);
                return;
            }
            catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == audclntEBufferSizeNotAligned)
            {
                var aligned = (period / Period10Ms + 1) * Period10Ms;
                client.Initialize(AudioClientShareMode.Exclusive, AudioClientStreamFlags.EventCallback,
                    aligned, aligned, format, Guid.Empty);
                return;
            }
        }
    }
}
