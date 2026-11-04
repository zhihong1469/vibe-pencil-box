// <copyright file="ExclusiveRenderEngine.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using PencilBox.Core.Audio;

namespace PencilBox.Infrastructure.Audio;

/// <summary>独占渲染引擎落位结果（协商/回退/欠载台账——回执 §4-#2 纪律：失败原因入日志逐设备留账）。</summary>
/// <param name="Exclusive">true=独占成位；false=已回退共享模式</param>
/// <param name="FormatText">实际格式（如 "48kHz/2ch/16bit 独占"）</param>
/// <param name="FallbackReason">回退原因（独占失败时非空）</param>
/// <param name="DeviceName">渲染端点名</param>
/// <param name="Underruns">读空欠载次数（累计）</param>
public sealed record ExclusiveRenderReport(
    bool Exclusive, string FormatText, string? FallbackReason, string DeviceName, long Underruns);

/// <summary>
/// WASAPI 独占渲染引擎（08 号方案 M3，吃满 Windows 音频引擎税的唯一大头）：
/// 16k/mono 解码 PCM 入队 → 重采样到端点独占格式 → <see cref="WasapiOut"/>（ShareMode.Exclusive）直出。
/// <list type="bullet">
/// <item>格式协商=候选表逐个 <c>IsFormatSupported(Exclusive)</c>（采集侧 ExclusiveCaptureEngine 同款模式）；
///       渲染端独占支持的格式由设备/驱动原生暴露——Realtek 已知只给 48k/2ch（t7 B-1 实证），候选表 48k 优先。</item>
/// <item>全候选失败 → 自动回退共享模式跑通（链路先通、引擎税后收），回退原因入台账。</item>
/// <item>独占=独占端点：期间本机其他声音被顶掉（联测纪律，操作单声明）。</item>
/// <item>无处理链（无 AEC/ANS/带通）——独占轨是纯渲染延迟 A/B 腿，不做语音处理。</item>
/// </list>
/// 线程：WritePcm（HID/BLE 收流线程）与渲染回调（NAudio 线程）经锁互斥；16k×240B/7.5ms 量级锁开销可忽略。
/// </summary>
public sealed class ExclusiveRenderEngine : IDisposable
{
    /// <summary>渲染端独占候选格式（48k/2ch/16bit 优先=Realtek 已知族；16k 保底防 UAC1.0 窄协商）</summary>
    public static readonly WaveFormat[] CandidateFormats =
    {
        new(48000, 16, 2),
        new(48000, 16, 1),
        new(44100, 16, 2),
        new(44100, 16, 1),
        new(16000, 16, 1),
        new(16000, 16, 2),
    };

    /// <summary>队列容量上界 200ms @16k（超出丢最旧保新鲜——延迟有界优先于完整）</summary>
    public const int QueueCapacitySamples = 3200;

    private const int SourceRate = 16000;

    private MMDevice? _device;
    private WasapiOut? _player;
    private readonly object _lock = new();
    private readonly Queue<float> _queue = new();
    private SoftLimiter? _limiter;
    private float _volume = 1f;
    private long _underruns;

    public bool Exclusive { get; private set; }
    public string FormatText { get; private set; } = "—";
    public string? FallbackReason { get; private set; }
    public string DeviceName => _device?.FriendlyName ?? "—";
    public long Underruns { get { lock (_lock) return _underruns; } }

    /// <summary>
    /// 开引擎：候选格式协商 → 独占成位；全败自动回退共享（不抛）。
    /// </summary>
    /// <param name="renderDevice">渲染端点（所有权移交本引擎，Dispose 时释放）</param>
    /// <param name="volume">软件增益（&gt;1x 走 SoftLimiter 防破音）</param>
    /// <param name="latencyMs">渲染缓冲（独占可给 15~20；共享建议 ≥40）</param>
    public void Start(MMDevice renderDevice, float volume, int latencyMs)
    {
        _device = renderDevice;
        _volume = volume;
        _limiter = new SoftLimiter(1, SourceRate);

        // 1) 独占格式协商（采集侧 ExclusiveCaptureEngine 同款候选探测）
        var chosen = CandidateFormats.FirstOrDefault(f =>
        {
            using var probe = renderDevice.AudioClient;
            return probe.IsFormatSupported(AudioClientShareMode.Exclusive, f);
        });

        if (chosen != null)
        {
            try
            {
                _player = new WasapiOut(renderDevice, AudioClientShareMode.Exclusive, useEventSync: true, latencyMs);
                _player.Init(BuildChain(chosen.SampleRate, chosen.Channels));
                _player.Play();
                Exclusive = true;
                FormatText = $"{chosen.SampleRate}kHz/{chosen.Channels}ch/16bit 独占";
                return;
            }
            catch (Exception ex)
            {
                // 协商过了但 Init 失败（事件同步/周期不兼容等）→ 记账后走共享回退
                FallbackReason = $"独占 Init 失败（{ex.GetType().Name}: {ex.Message}）";
                try { _player?.Dispose(); } catch { /* 收尾容错 */ }
                _player = null;
            }
        }
        else
        {
            FallbackReason = "端点独占模式不支持候选格式（48k/44.1k/16k × mono/stereo s16）";
        }

        // 2) 共享回退：端点混音格式自适应（SoftReceiverProbe 同款三件套）
        var mixFormat = renderDevice.AudioClient.MixFormat;
        _player = new WasapiOut(renderDevice, AudioClientShareMode.Shared, useEventSync: true, Math.Max(latencyMs, 40));
        _player.Init(BuildChain(mixFormat.SampleRate, mixFormat.Channels));
        _player.Play();
        Exclusive = false;
        FormatText = $"{mixFormat.SampleRate}kHz/{mixFormat.Channels}ch 共享（回退）";
    }

    /// <summary>组装 16k mono 源 → 重采样 → 声道扩展（目标端点格式；增益/限幅在源内做）。</summary>
    private IWaveProvider BuildChain(int targetRate, int targetChannels)
    {
        ISampleProvider current = new QueueSourceProvider(this);
        current = new WdlResamplingSampleProvider(current, targetRate);
        if (targetChannels >= 2)
            current = new MonoToStereoSampleProvider(current);
        return new SampleToWaveProvider16(current);
    }

    /// <summary>解码 PCM 入队（16k mono s16LE → float；超容量丢最旧保新鲜）。</summary>
    public void WritePcm(byte[] pcm16kMono)
    {
        lock (_lock)
        {
            for (int i = 0; i + 1 < pcm16kMono.Length; i += 2)
            {
                if (_queue.Count >= QueueCapacitySamples)
                    _queue.Dequeue();   // 丢最旧保新鲜
                _queue.Enqueue(((short)(pcm16kMono[i] | (pcm16kMono[i + 1] << 8))) / 32768f);
            }
        }
    }

    public void SetVolume(float volume)
    {
        lock (_lock)
            _volume = volume;
    }

    public ExclusiveRenderReport GetReport() =>
        new(Exclusive, FormatText, FallbackReason, DeviceName, Underruns);

    public void Dispose()
    {
        try { _player?.Stop(); } catch { /* 收尾容错 */ }
        _player?.Dispose();
        _player = null;
        _device?.Dispose();
        _device = null;
        lock (_lock)
            _queue.Clear();
    }

    /// <summary>
    /// 队列源（16k mono float ISampleProvider）：读空补零防流停 + 欠载计数；
    /// 增益 × SoftLimiter 在此单点做（重采样/声道扩展之前，幅度域正确）。
    /// </summary>
    private sealed class QueueSourceProvider : ISampleProvider
    {
        private readonly ExclusiveRenderEngine _owner;

        public QueueSourceProvider(ExclusiveRenderEngine owner) => _owner = owner;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SourceRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            int n;
            lock (_owner._lock)
            {
                n = 0;
                while (n < count && _owner._queue.Count > 0)
                    buffer[offset + n++] = _owner._queue.Dequeue() * _owner._volume;
                if (_owner._limiter != null && n > 0)
                    _owner._limiter.Process(buffer.AsSpan(offset, n));
                if (n < count)
                    _owner._underruns++;   // 计事件不计样本（一次读空=一次欠载）
            }
            for (int i = n; i < count; i++)
                buffer[offset + i] = 0f;   // 补零防 NAudio 视为流结束（返回 0=Stop）
            return count;
        }
    }
}
