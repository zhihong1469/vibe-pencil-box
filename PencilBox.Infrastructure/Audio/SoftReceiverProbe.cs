// <copyright file="SoftReceiverProbe.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using NAudio.CoreAudioApi;
using NAudio.Wave;
using PencilBox.Core.Ble.SoftReceiver;
using PencilBox.Core.Dsp;

namespace PencilBox.Infrastructure.Audio;

/// <summary>probe 结果（走表剖面 + chirp 扫描 + 留档路径）。</summary>
/// <param name="Pacing">帧到达走表剖面（PPM/p50/p95/max 偏差）</param>
/// <param name="Scan">chirp 网格扫描（形状/PPM 口径）</param>
/// <param name="AudioFrames">采集到的解码音频帧数</param>
/// <param name="TotalPcmBytes">解码 PCM 总字节（16k/16bit/mono）</param>
/// <param name="WavPath">解码流 WAV 留档路径</param>
/// <param name="RenderDeviceName">chirp 发射渲染设备名</param>
public sealed record SoftReceiverProbeReport(
    StreamPacingStats Pacing,
    ChirpScanReport Scan,
    long AudioFrames,
    long TotalPcmBytes,
    string WavPath,
    string RenderDeviceName);

/// <summary>
/// 软件接收器延迟实测探针（CLI <c>srx probe</c> 与 Desktop「延迟实测」共用一份逻辑）：
/// 挂 <see cref="SoftReceiverSession.AudioFrameReady"/> 采集（帧计数 + 到达时刻 + PCM），
/// 同时默认渲染设备播 chirp 训练列（750ms 周期，固件同源表，笔端麦拾取→BLE 回传闭环）。
/// <see cref="StopAndAnalyze"/> = 停播 + 走表拟合（StreamPacingAnalyzer）+ chirp 网格扫描
/// （ChirpScanAnalyzer）+ WAV 留档。
/// 口径：绝对延迟含起点相位常量偏置（笔端自由跑钟无共享锚），同场内形状/PPM 有效、
/// 跨场对账勿比绝对值；运行前提=语音流在流（K2/S1 物理触发）。
/// ⚠ 匹配对象硬注（08 号回执 §3A）：收到并解码的信号 D(t)=decode(encode(房间里的 chirp))，
/// 模板是发射原信号 T(t)——互相关峰位含 mSBC 编解码往返群延迟（8 子带滤波器组，
/// ≈10ms 量级**常量**）。A/B 两腿（UAC/HID）同笔同编解码器 → 常量共模抵消，A/B 判据不受影响；
/// 但任何"绝对延迟逐项扣减求纯设备延迟"的账本必须把这笔群延迟算进去，漏扣即错账
/// （标定手段=port SBC 编码器离线预演 mSBC(T) 作第二模板，两峰位差=群延迟；仓内暂无编码器，列观察项）。
/// 线程：AudioFrameReady 在 WinRT 线程池回调，采集内部锁保护；Start/Stop 由调用方串行驱动。
/// </summary>
public sealed class SoftReceiverProbe : IDisposable
{
    /// <summary>低于此帧数视为语音流未在流，不分析不落盘</summary>
    public const int MinAudioFrames = 8;

    private readonly SoftReceiverSession _session;
    private readonly List<byte[]> _pcmChunks = [];
    private readonly List<(long Seq, double ArrivalMs)> _pacingFrames = [];
    private readonly object _lock = new();
    private long _frameIndex;
    private MMDevice? _renderDevice;
    private WasapiOut? _player;
    private bool _attached;
    private bool _disposed;

    public SoftReceiverProbe(SoftReceiverSession session) => _session = session;

    /// <summary>chirp 发射渲染设备名（Start 后有效）</summary>
    public string? RenderDeviceName { get; private set; }

    /// <summary>已采集解码帧数（实时快照）</summary>
    public long CapturedFrames
    {
        get { lock (_lock) return _pacingFrames.Count; }
    }

    public bool IsPlaying { get; private set; }

    /// <summary>
    /// 开播 chirp 训练列并挂帧采集（16k mono → 端点混音格式重采样/声道转换）。
    /// 抛出异常（默认渲染设备缺失/WasapiOut 初始化失败等）时未开播未挂钩，可安全丢弃。
    /// </summary>
    public void Start(float volume)
    {
        using (var enumerator = new MMDeviceEnumerator())
            _renderDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        RenderDeviceName = _renderDevice.FriendlyName;
        var chirpSource = new ChirpTrainSource();
        var mixFormat = _renderDevice.AudioClient.MixFormat;
        var resampled = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(chirpSource, mixFormat.SampleRate);
        var gain = new NAudio.Wave.SampleProviders.VolumeSampleProvider(resampled) { Volume = volume };
        ISampleProvider feed = mixFormat.Channels >= 2
            ? new NAudio.Wave.SampleProviders.MonoToStereoSampleProvider(gain)
            : gain;
        _player = new WasapiOut(_renderDevice, AudioClientShareMode.Shared, useEventSync: true, 40);
        _player.Init(new NAudio.Wave.SampleProviders.SampleToWaveProvider(feed));
        _session.AudioFrameReady += OnAudioFrame;
        _attached = true;
        _player.Play();
        IsPlaying = true;
    }

    private void OnAudioFrame(object? sender, SoftReceiverAudioFrame f)
    {
        lock (_lock)
        {
            // 走表 x 轴 = 解码帧计数（每帧标称 7.5ms）。
            // 真机实证（2026-11-02 首次在流实测）：AudioSeq 是**通知级**序号（每 DATA +1，
            // 通知内 N=2 帧同 seq）——按 seq 做走表会把 15ms 通知节拍读成 2× 标称步长
            // （PPM 伪值 ≈+1e6）。帧计数不受打包影响；丢帧时斜率失真可由 seq 跳/缺口另察。
            _pacingFrames.Add((_frameIndex++, f.LocalTimestampMs));
            _pcmChunks.Add(f.Pcm);
        }
    }

    /// <summary>
    /// 停播 + 摘钩 + 走表拟合 + chirp 扫描 + WAV 留档（目录不存在则建）。
    /// 采集帧不足 <see cref="MinAudioFrames"/>（语音流未在流）返回 null，不落盘。
    /// </summary>
    public SoftReceiverProbeReport? StopAndAnalyze(string wavPath)
    {
        StopPlayback();
        List<byte[]> pcm;
        List<(long Seq, double ArrivalMs)> pacing;
        lock (_lock)
        {
            Detach();
            pcm = _pcmChunks.ToList();
            pacing = _pacingFrames.ToList();
        }
        if (pacing.Count < MinAudioFrames)
            return null;

        var totalPcmBytes = pcm.Sum(c => c.Length);
        var t0 = pacing[0].ArrivalMs;
        var pacingStats = StreamPacingAnalyzer.Analyze(
            pacing.Select(f => (f.Seq, f.ArrivalMs - t0)).ToList(), 7.5);

        var signal = new short[totalPcmBytes / 2];
        var pos = 0;
        foreach (var chunk in pcm)
        {
            Buffer.BlockCopy(chunk, 0, signal, pos, chunk.Length);
            pos += chunk.Length;
        }
        var scan = ChirpScanAnalyzer.ScanByGrid(signal, MsbcDecoder.SampleRate, ChirpTemplate.PeriodSamples);

        var dir = Path.GetDirectoryName(Path.GetFullPath(wavPath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        using (var writer = new WaveFileWriter(wavPath, new WaveFormat(MsbcDecoder.SampleRate, 16, 1)))
        {
            foreach (var chunk in pcm)
                writer.Write(chunk);
        }

        return new SoftReceiverProbeReport(
            pacingStats, scan, pacing.Count, totalPcmBytes,
            Path.GetFullPath(wavPath), RenderDeviceName ?? "—");
    }

    /// <summary>中止：停播 + 摘钩（保采集数据不分析）——断开/取消路径用。</summary>
    public void Abort()
    {
        StopPlayback();
        lock (_lock)
            Detach();
    }

    private void Detach()
    {
        if (_attached)
        {
            _session.AudioFrameReady -= OnAudioFrame;
            _attached = false;
        }
    }

    private void StopPlayback()
    {
        IsPlaying = false;
        if (_player != null)
        {
            try { _player.Stop(); } catch { /* 收尾容错 */ }
            _player.Dispose();
            _player = null;
        }
        if (_renderDevice != null)
        {
            _renderDevice.Dispose();
            _renderDevice = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Abort();
    }
}
