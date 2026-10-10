// <copyright file="LatencyProbeEngine.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using PencilBox.Core.Dsp;

namespace PencilBox.Infrastructure.Audio;

/// <summary>一次延迟探测的原始产物（互相关与统计在调用侧做，便于复用与导出）</summary>
public sealed record LatencyProbeResult(
    string CaptureDevice,
    string RenderDevice,
    int CaptureRate,
    int RenderRate,
    int RenderChannels,
    long PlayQpc,
    long CaptureQpc,
    short[] CapturedMono,
    int[] PulsePositionsRender,
    double RenderLatencyMs);

/// <summary>脉冲互相关的统计结论（CLI 与 Desktop 共用同一口径）</summary>
public sealed record LatencyProbeStats(
    int Expected,
    int Located,
    IReadOnlyList<double> PerPulseMs,
    double P50,
    double P95,
    double Min,
    double Max,
    double CapturedSeconds,
    string CaptureDesc,
    string RenderDesc,
    int RenderLatencySubtractedMs)
{
    /// <summary>抖动 = p95 − p50</summary>
    public double Jitter => P95 - P50;
}

/// <summary>
/// 端到端延迟探测引擎（box/03 方案 C 的落地底座，box/07 §2.1）：
/// 扬声器播预生成的扫频脉冲串 → 采集端（麦克风/UAC 流）同录 → 两侧 QPC 锚点 + 互相关定位
/// 得到"播出→到达"闭环延迟。自校准时播/录本机双端点即可跑通全链算法，
/// 设备端（笔+基站）接入后同一套设施直接测 P3/M3。
/// 一次 Run 一个全新实例状态；非线程安全。
/// </summary>
public sealed class LatencyProbeEngine(ILogger logger)
{
    /// <param name="captureSelector">采集端点（ID 或友好名称子串；null/""/"default"=系统默认）</param>
    /// <param name="renderSelector">渲染端点（同上）</param>
    /// <param name="pulses">脉冲数（默认 20）</param>
    /// <param name="intervalMs">脉冲间隔（默认 500ms，须大于预期最大延迟+混响尾）</param>
    /// <param name="pulseMs">单脉冲时长（默认 15ms）</param>
    /// <param name="amplitude">脉冲幅度 0~1</param>
    /// <param name="renderLatencyMs">WasapiOut 渲染缓冲（也作为播出的固定偏移参考值返回）</param>
    /// <param name="captureRate">采集请求采样率（16k 与 UAC 流同格式；不支持时自动回退设备混音格式）</param>
    /// <param name="progress">进度回调（可空）</param>
    public async Task<LatencyProbeResult> RunAsync(
        string? captureSelector, string? renderSelector,
        int pulses = 20, int intervalMs = 500, int pulseMs = 15,
        float amplitude = 0.8f, int renderLatencyMs = 40, int captureRate = 16000,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        if (pulses < 1) throw new ArgumentOutOfRangeException(nameof(pulses));
        Say(progress, "解析端点…");
        using var enumerator = new MMDeviceEnumerator();
        using var captureDevice = ResolveDevice(enumerator, DataFlow.Capture, captureSelector, "录音");
        using var renderDevice = ResolveDevice(enumerator, DataFlow.Render, renderSelector, "播放");

        // 渲染：共享模式必须喂端点混音格式（与扩音引擎同一约束）
        WaveFormat outFormat;
        using (var client = renderDevice.AudioClient)
            outFormat = client.MixFormat;
        var isFloat = IsFloatFormat(outFormat);
        if (!isFloat && outFormat.BitsPerSample != 16)
            throw new NotSupportedException($"渲染端点混音格式不受支持（仅 float32/PCM16）: {outFormat}");

        // 生成脉冲串（渲染率）并编码为端点格式字节
        var chirp = PulseTrain.GenerateChirp(outFormat.SampleRate, pulseMs, amplitude: amplitude);
        var (signal, positions) = PulseTrain.Build(chirp, outFormat.SampleRate, pulses, intervalMs);
        var playBytes = Encode(signal, outFormat, isFloat);
        var renderDurMs = signal.Length * 1000.0 / outFormat.SampleRate;

        // 采集：请求 16k/16/1，失败回退设备混音格式（级联口径与扩音引擎一致）
        WaveFormat requested = new(Math.Clamp(captureRate, 8000, 96000), 16, 1);
        WaveFormat captureMix;
        using (var client = captureDevice.AudioClient)
            captureMix = client.MixFormat;

        var captured = new MemoryStream();
        long captureQpc = 0;
        var captureDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WasapiCapture? capture = null;
        WaveFormat inFormat = requested;

        void OnData(object? s, WaveInEventArgs e)
        {
            if (Volatile.Read(ref captureQpc) == 0)
                Volatile.Write(ref captureQpc, Stopwatch.GetTimestamp());
            captured.Write(e.Buffer, 0, e.BytesRecorded);
        }
        void OnStopped(object? s, StoppedEventArgs e)
        {
            if (e.Exception != null) captureDone.TrySetException(e.Exception);
            else captureDone.TrySetResult();
        }

        WasapiCapture? TryStart(WaveFormat format, bool eventSync)
        {
            var candidate = new WasapiCapture(captureDevice, eventSync, 20);
            candidate.DataAvailable += OnData;
            candidate.RecordingStopped += OnStopped;
            try
            {
                candidate.WaveFormat = format;
                candidate.StartRecording();
                return candidate;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "探测采集格式 {Format} eventSync={Sync} 启动失败，尝试回退", format, eventSync);
                candidate.DataAvailable -= OnData;
                candidate.RecordingStopped -= OnStopped;
                candidate.Dispose();
                return null;
            }
        }

        Say(progress, $"启动采集 [{captureDevice.FriendlyName}]…");
        capture = TryStart(requested, true) ?? TryStart(captureMix, true)
                  ?? TryStart(requested, false) ?? TryStart(captureMix, false)
                  ?? throw new InvalidOperationException("采集端点所有格式组合均启动失败");
        inFormat = capture.WaveFormat;
        if (inFormat.BitsPerSample != 16 || inFormat.Channels != 1)
            throw new NotSupportedException($"探测要求 16bit 单声道采集，实得 {inFormat}（回退格式含转换，暂不支持）");

        WasapiOut? output = null;
        try
        {
            await Task.Delay(250, ct);                       // 让采集流先稳定，避免起点抖动

            Say(progress, $"播放 {pulses} 脉冲（{intervalMs}ms 间隔，渲染 {outFormat.SampleRate}Hz/{outFormat.Channels}ch {(isFloat ? "float" : "pcm16")}）…");
            var source = new StaticSource(playBytes, outFormat);
            output = new WasapiOut(renderDevice, AudioClientShareMode.Shared, useEventSync: true, Math.Clamp(renderLatencyMs, 10, 200));
            output.Init(source);
            var playQpc = Stopwatch.GetTimestamp();
            output.Play();

            await Task.Delay((int)renderDurMs + 400, ct);    // 脉冲串 + 混响尾

            Say(progress, "停止…");
            output.Stop();
            capture.StopRecording();
            await captureDone.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
            if (Volatile.Read(ref captureQpc) == 0)
                throw new InvalidOperationException("采集流未收到任何数据（端点选错或被占用/拦截）");

            var mono = new short[(int)(captured.Length / 2)];
            var bytes = captured.GetBuffer();
            Buffer.BlockCopy(bytes, 0, mono, 0, (int)captured.Length);

            Say(progress, $"采集完成：{mono.Length / (double)inFormat.SampleRate:F1}s @ {inFormat.SampleRate}Hz");
            return new LatencyProbeResult(
                captureDevice.FriendlyName,
                renderDevice.FriendlyName,
                inFormat.SampleRate,
                outFormat.SampleRate,
                outFormat.Channels,
                playQpc,
                Volatile.Read(ref captureQpc),
                mono,
                positions,
                Math.Clamp(renderLatencyMs, 10, 200));
        }
        finally
        {
            if (capture != null)
            {
                capture.DataAvailable -= OnData;
                capture.RecordingStopped -= OnStopped;
                capture.Dispose();
            }
            output?.Dispose();
        }
    }

    /// <summary>
    /// 对探测原始产物做互相关定位与统计（CLI 与 Desktop 共用，防两处口径漂移）：
    /// 模板与播放同形（按采集率再生成）→ 归一化互相关贪心取峰 → 逐脉冲"到达−发出"。
    /// 默认扣掉固定测量口径 <see cref="MeasurementConventionMs"/>（脉冲是空缓冲启动后的一次性
    /// 突发，出声时刻取决于流内位置而非缓冲深度，故渲染缓冲设置不参与扣减——2026-10-10 实测定案），
    /// <paramref name="raw"/> 看原始闭环。
    /// </summary>
    public const int MeasurementConventionMs = 40;

    public static LatencyProbeStats Analyze(
        LatencyProbeResult result, int expectedPulses, int intervalMs, int pulseMs, bool raw = false)
    {
        var template = PulseTrain.GenerateChirp(result.CaptureRate, pulseMs);
        var intervalSamples = result.CaptureRate * intervalMs / 1000;
        // ±20ms 跟踪窗：覆盖运行内格点漂移（钟差 ppm 级），排除 +35ms 量级的房间反射
        var windowSamples = Math.Min(result.CaptureRate * 20 / 1000, intervalSamples / 4);
        var slots = PulseLocator.LocateTracked(result.CapturedMono, template, expectedPulses, intervalSamples, windowSamples);

        var qpf = (double)Stopwatch.Frequency;
        var baseOffsetMs = (result.CaptureQpc - result.PlayQpc) / qpf * 1000.0 - (raw ? 0 : MeasurementConventionMs);
        var delays = new List<double>();
        for (var k = 0; k < Math.Min(slots.Count, result.PulsePositionsRender.Length); k++)
        {
            if (slots[k] is not { } hit) continue;          // 该槽质量不足/越界，跳过
            var arrivalMs = baseOffsetMs + hit.PositionSamples * 1000.0 / result.CaptureRate;
            var emittedMs = result.PulsePositionsRender[k] * 1000.0 / result.RenderRate;
            delays.Add(arrivalMs - emittedMs);
        }

        var capturedSeconds = result.CapturedMono.Length / (double)result.CaptureRate;
        if (delays.Count == 0)
            return new LatencyProbeStats(expectedPulses, 0, [], 0, 0, 0, 0, capturedSeconds,
                $"{result.CaptureDevice} @ {result.CaptureRate}Hz",
                $"{result.RenderDevice} @ {result.RenderRate}Hz/{result.RenderChannels}ch，测试缓冲 {result.RenderLatencyMs}ms",
                raw ? 0 : MeasurementConventionMs);

        var sorted = delays.Order().ToArray();
        double P(double q) => sorted[(int)Math.Clamp(q * (sorted.Length - 1), 0, sorted.Length - 1)];
        return new LatencyProbeStats(
            expectedPulses, delays.Count, delays,
            P(0.5), P(0.95), sorted[0], sorted[^1], capturedSeconds,
            $"{result.CaptureDevice} @ {result.CaptureRate}Hz",
            $"{result.RenderDevice} @ {result.RenderRate}Hz/{result.RenderChannels}ch，测试缓冲 {result.RenderLatencyMs}ms",
            raw ? 0 : MeasurementConventionMs);
    }

    private static byte[] Encode(short[] mono, WaveFormat format, bool isFloat)
    {
        var ch = format.Channels;
        byte[] bytes;
        if (isFloat)
        {
            bytes = new byte[mono.Length * ch * 4];
            for (var i = 0; i < mono.Length; i++)
            {
                var v = mono[i] / 32768f;
                for (var c = 0; c < ch; c++)
                    BitConverter.TryWriteBytes(bytes.AsSpan((i * ch + c) * 4), v);
            }
        }
        else
        {
            bytes = new byte[mono.Length * ch * 2];
            for (var i = 0; i < mono.Length; i++)
                for (var c = 0; c < ch; c++)
                    BitConverter.TryWriteBytes(bytes.AsSpan((i * ch + c) * 2), mono[i]);
        }
        return bytes;
    }

    private static bool IsFloatFormat(WaveFormat format) =>
        format.Encoding == WaveFormatEncoding.IeeeFloat ||
        (format is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00AA00389B71"));

    private static MMDevice ResolveDevice(MMDeviceEnumerator enumerator, DataFlow flow, string? selector, string label)
    {
        if (string.IsNullOrWhiteSpace(selector) || selector.Equals("default", StringComparison.OrdinalIgnoreCase))
            return enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
        try
        {
            return enumerator.GetDevice(selector);
        }
        catch (Exception)
        {
            // fallthrough：按友好名称子串匹配
        }
        return enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)
                   .FirstOrDefault(d => d.FriendlyName.Contains(selector, StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException($"未找到匹配的{label}端点: {selector}");
    }

    private static void Say(Action<string>? progress, string msg) => progress?.Invoke(msg);

    /// <summary>整段预生成音频的只读拉取源</summary>
    private sealed class StaticSource(byte[] data, WaveFormat format) : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = format;
        private int _pos;

        public int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(count, data.Length - _pos);
            if (n > 0)
            {
                Buffer.BlockCopy(data, _pos, buffer, offset, n);
                _pos += n;
            }
            if (n < count)
                Array.Clear(buffer, offset + n, count - n);   // 尾部静音，避免循环杂音
            return count;
        }
    }
}
