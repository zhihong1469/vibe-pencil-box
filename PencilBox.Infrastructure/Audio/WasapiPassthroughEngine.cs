// <copyright file="WasapiPassthroughEngine.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using PencilBox.Core.Audio;
using PencilBox.Core.Dsp;
using PencilBox.Core.Models;
using PencilBox.Core.Services;

namespace PencilBox.Infrastructure.Audio;

/// <summary>
/// WASAPI 扩音直通引擎：采集（事件驱动共享模式）→ 零前视格式转换 → 环形缓冲（丢最旧）
/// → 渲染拉取（事件驱动，欠载补静音）。与录音路径（NAudioAudioCapture）完全独立。
/// 附加延迟 ≈ 采集引擎周期(~10ms) + 环形深度(RingDepthMs，活量) + 渲染缓冲(RenderLatencyMs)。
/// </summary>
public sealed class WasapiPassthroughEngine : IAudioPassthrough
{
    private readonly ILogger<WasapiPassthroughEngine> _logger;

    private readonly object _gate = new();

    private WasapiCapture? _capture;
    private WasapiOut? _output;
    private MMDevice? _captureDevice;
    private MMDevice? _renderDevice;
    private PassthroughSource? _source;
    private PcmRingBuffer? _ring;
    private SampleFormatConverter? _converter;
    private byte[] _convertScratch = Array.Empty<byte>();

    private WaveFormat? _inFormat;
    private WaveFormat? _outFormat;
    private string _captureName = string.Empty;
    private string _renderName = string.Empty;
    private int _renderLatencyMs;
    private float _volume = 1.0f;
    // 启动淡入：AEC 参考历史填满前走旁路（裸环），若开局环增益已过临界（如上一场啸叫泵高了
    // 麦 AGC），旁路窗口内即起啸且饱和后 AEC 难以再收敛；开局 300ms 线性淡入把窗口堵死。
    private int _fadeInLeft;
    private float _fadeInTotal = 1f;
    private SoftLimiter? _limiter;
    private float[] _limitScratch = Array.Empty<float>();
    private Stopwatch? _clock;

    private AudioPassthroughState _state = AudioPassthroughState.Idle;

    // 计数（多线程：采集线程 / 渲染线程 / 调用方，全部 Interlocked）
    private long _captureCallbacks;
    private long _captureBytes;
    private long _renderPulls;
    private long _renderBytes;
    private long _underruns;
    private long _depthSumMsX1000;
    private long _depthSamples;
    private long _depthMaxMsX1000;
    private int _levelX1000;
    private int _limiterGainX1000 = 1000;

    // === 处理链（AEC/ANS/AFC，16k 单声道 PCM16 定帧 160 样本 = 10ms）===
    // 参考链：渲染拉取处取"实际送出的声音"（含音量+限制器）→ 降采 16k → ShortDelayRing；
    // 采集链：mic 降采 16k → 凑帧 → 对齐取参考 → AEC/ANS → 移频 → 出格式转换 → 环形缓冲。
    private IEchoProcessor? _echo;
    private SampleFormatConverter? _micNorm;
    private SampleFormatConverter? _chainOut;
    private SampleFormatConverter? _refDown;
    private byte[] _micNormScratch = Array.Empty<byte>();
    private byte[] _chainOutScratch = Array.Empty<byte>();
    private byte[] _refDownScratch = Array.Empty<byte>();
    private byte[] _micFrameBytes = new byte[320];
    private FrameStitcher? _micStitcher;
    private ShortDelayRing? _refRing;
    private EchoDelayEstimator? _estimator;
    private FreqShifter? _shifter;
    private readonly short[] _micFrame = new short[ChainFrameSamples];
    private readonly short[] _refFrame = new short[ChainFrameSamples];
    private readonly short[] _dspFrame = new short[ChainFrameSamples];
    private readonly short[] _refWide = new short[ChainFrameSamples + 2 * ChainSearchSamples];
    private long _refLagNominal;
    private int _aecOn = 1;
    private int _ansOn = 1;
    private int _afcOn;
    private int _aecGateOn;
    private int _chainReady;
    private AecSilenceGate? _aecGate;
    private long _gateBypassFrames;

    // 调参状态（自动档=内置推荐；手动档用用户值），Start 参数与运行中 Set*Params 共用
    private bool _aecAuto = true, _ansAuto = true;
    private int _aecSuppressDb = 40, _aecSuppressActiveDb = 15, _ansNoiseSuppressDb = 15;
    private long _dspMaxNs;
    private long _dspOverruns;
    private readonly Stopwatch _dspClock = new();

    private const int ChainFrameSamples = 160;   // 10ms @16k
    private const int ChainSearchSamples = 160;  // 对齐搜索半径 10ms
    private const int ChainTailSamples = 1600;   // AEC 自适应滤波器尾长 100ms

    private readonly Stopwatch _statsThrottle = Stopwatch.StartNew();
    private long _lastStatsThrottleMs;

    public AudioPassthroughState State { get { lock (_gate) return _state; } }

    public event EventHandler<AudioPassthroughState>? StateChanged;
    public event EventHandler<AudioPassthroughStats>? StatsUpdated;

    public WasapiPassthroughEngine(ILogger<WasapiPassthroughEngine> logger)
    {
        _logger = logger;
    }

    public Task<IReadOnlyList<AudioDeviceInfo>> GetCaptureDevicesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(AudioDeviceEnumerator.GetCaptureDevices());

    public Task<IReadOnlyList<AudioDeviceInfo>> GetRenderDevicesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(AudioDeviceEnumerator.GetRenderDevices());

    public Task StartAsync(PassthroughStartOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_gate)
        {
            if (_state is AudioPassthroughState.Running or AudioPassthroughState.Starting)
                throw new InvalidOperationException("扩音直通已在运行，请先停止");
            _state = AudioPassthroughState.Starting;
        }
        RaiseStateChanged();

        // 启动参数中的处理链开关 → 运行时状态位（运行中 Set*Enabled 也走同一位）
        Interlocked.Exchange(ref _aecOn, options.EnableAec ? 1 : 0);
        Interlocked.Exchange(ref _ansOn, options.EnableAns ? 1 : 0);
        Interlocked.Exchange(ref _afcOn, options.EnableAfc ? 1 : 0);
        Interlocked.Exchange(ref _aecGateOn, options.EnableAecGate ? 1 : 0);
        Interlocked.Exchange(ref _gateBypassFrames, 0);
        _aecAuto = options.AecAuto; _ansAuto = options.AnsAuto;
        _aecSuppressDb = options.AecSuppressDb; _aecSuppressActiveDb = options.AecSuppressActiveDb;
        _ansNoiseSuppressDb = options.AnsNoiseSuppressDb;

        try
        {
            StartCore(options);
            lock (_gate) _state = AudioPassthroughState.Running;
            RaiseStateChanged();
            _logger.LogInformation(
                "扩音直通已启动: {In} -> {Out}, 渲染缓冲 {Latency}ms, 音量 {Volume:F2}",
                _captureName, _renderName, _renderLatencyMs, _volume);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "扩音直通启动失败");
            CleanupStreams();
            lock (_gate) _state = AudioPassthroughState.Stopped;
            RaiseStateChanged();
            throw;
        }

        return Task.CompletedTask;
    }

    private void StartCore(PassthroughStartOptions options)
    {
        // 1. 解析设备
        using var enumerator = new MMDeviceEnumerator();
        _captureDevice = ResolveDevice(enumerator, DataFlow.Capture, options.CaptureDeviceId, "录音");
        _renderDevice = ResolveDevice(enumerator, DataFlow.Render, options.RenderDeviceId, "播放");
        _captureName = _captureDevice.FriendlyName;
        _renderName = _renderDevice.FriendlyName;
        _renderLatencyMs = Math.Clamp(options.RenderLatencyMs, 10, 200);
        _volume = Math.Clamp(options.Volume, 0f, PassthroughStartOptions.MaxVolume);

        // 2. 渲染格式：共享模式必须喂端点混音格式（NAudio 未启用 AUTOCONVERT）
        WaveFormat outFormat;
        using (var audioClient = _renderDevice.AudioClient)
        {
            outFormat = audioClient.MixFormat;
        }
        if (!IsFloatFormat(outFormat) && outFormat.BitsPerSample != 16)
            throw new NotSupportedException($"渲染端点混音格式不受支持（仅 float32/PCM16）: {outFormat}");
        _outFormat = outFormat;

        // 启动淡入窗口（300ms，按输出率折算样本）
        _fadeInTotal = MathF.Max(1f, outFormat.SampleRate * 0.3f);
        _fadeInLeft = (int)_fadeInTotal;

        // 3. 采集：级联尝试（请求格式 → 采集端混音格式 × 事件同步 → 睡眠同步）。
        // 实机坑：USB Audio 1.0 + 事件同步 + 非混音格式 → AudioClient.Initialize 阻塞 ~30s 后
        // E_UNEXPECTED(0x8000FFFF)；一旦出现该码，跳过剩余事件同步尝试，直落睡眠同步。
        var requested = new WaveFormat(Math.Clamp(options.SampleRate, 8000, 96000), 16, Math.Clamp(options.Channels, 1, 2));
        WaveFormat captureMix;
        using (var captureClient = _captureDevice.AudioClient)
        {
            captureMix = captureClient.MixFormat;
        }

        WasapiCapture? capture = null;
        WaveFormat inFormat = requested;
        var sawCatastrophic = false;
        Exception? lastError = null;

        // 顺序：请求格式/混音格式 × 事件同步优先，睡眠同步兜底
        var attempts = new (WaveFormat Format, bool EventSync)[]
        {
            (requested, true), (captureMix, true), (requested, false), (captureMix, false)
        };

        foreach (var attempt in attempts)
        {
            if (sawCatastrophic && attempt.EventSync) continue;

            var candidate = new WasapiCapture(_captureDevice, attempt.EventSync, 20);
            candidate.DataAvailable += OnCaptureDataAvailable;
            candidate.RecordingStopped += OnCaptureStopped;
            try
            {
                candidate.WaveFormat = attempt.Format;
                candidate.StartRecording();
                capture = candidate;
                inFormat = attempt.Format;
                break;
            }
            catch (COMException cex) when (cex.HResult == unchecked((int)0x8000FFFF))
            {
                sawCatastrophic = true;
                lastError = cex;
                DetachAndDispose(candidate);
                _logger.LogWarning(cex, "采集启动 E_UNEXPECTED（{Format}, 事件同步={Event}），降级重试", attempt.Format, attempt.EventSync);
            }
            catch (Exception ex)
            {
                lastError = ex;
                DetachAndDispose(candidate);
                _logger.LogWarning(ex, "采集启动失败（{Format}, 事件同步={Event}），降级重试", attempt.Format, attempt.EventSync);
            }
        }

        if (capture == null)
            throw new NotSupportedException(
                $"采集流打开失败（已尝试 请求格式/混音格式 × 事件/睡眠同步）: {_captureName}", lastError);

        _capture = capture;
        _inFormat = inFormat;
        _converter = new SampleFormatConverter(
            inFormat.SampleRate, inFormat.BitsPerSample, IsFloatFormat(inFormat), inFormat.Channels,
            outFormat.SampleRate, outFormat.BitsPerSample, IsFloatFormat(outFormat), outFormat.Channels);
        _limiter = new SoftLimiter(outFormat.Channels, outFormat.SampleRate);

        // 4. 环形缓冲：600ms（渲染格式），溢出丢最旧
        _ring = new PcmRingBuffer(Math.Max(1, (int)(outFormat.AverageBytesPerSecond * 0.6)));
        _convertScratch = new byte[Math.Max(4096, _converter.MaxOutputBytesFor(inFormat.AverageBytesPerSecond / 10))];

        // 4.5 处理链（AEC/ANS/AFC）：16k 单声道 PCM16，定帧 10ms
        var chainTogglesOn = Volatile.Read(ref _aecOn) != 0 || Volatile.Read(ref _ansOn) != 0 || Volatile.Read(ref _afcOn) != 0;
        _micNorm = new SampleFormatConverter(
            inFormat.SampleRate, inFormat.BitsPerSample, IsFloatFormat(inFormat), inFormat.Channels, 16000, 16, false, 1);
        _chainOut = new SampleFormatConverter(16000, 16, false, 1,
            outFormat.SampleRate, outFormat.BitsPerSample, IsFloatFormat(outFormat), outFormat.Channels);
        _refDown = new SampleFormatConverter(
            outFormat.SampleRate, outFormat.BitsPerSample, IsFloatFormat(outFormat), outFormat.Channels, 16000, 16, false, 1);
        _micNormScratch = new byte[Math.Max(4096, _micNorm.MaxOutputBytesFor(inFormat.AverageBytesPerSecond / 10))];
        _chainOutScratch = new byte[Math.Max(4096, _chainOut.MaxOutputBytesFor(320))];
        _refDownScratch = new byte[Math.Max(4096, _refDown.MaxOutputBytesFor(outFormat.AverageBytesPerSecond / 10))];
        _micStitcher = new FrameStitcher(320);
        // 参考环容量需覆盖 最大渲染缓冲(200ms) + 余量(20ms) + 搜索(20ms) + 帧(10ms) ≈ 4000 样本
        _refRing = new ShortDelayRing(8192);
        _estimator = new EchoDelayEstimator(ChainFrameSamples, 16000, searchMs: 10, updateEveryFrames: 100);
        _shifter = new FreqShifter(16000, 5.0);
        _refLagNominal = _renderLatencyMs * 16L + 320;   // 渲染缓冲 + DAC/空气传播/ADC/周期余量(20ms)
        Interlocked.Exchange(ref _dspMaxNs, 0);
        Interlocked.Exchange(ref _dspOverruns, 0);
        if (chainTogglesOn && _echo == null)
        {
            try
            {
                _echo = new SpeexEchoProcessor(ChainFrameSamples, ChainTailSamples, 16000);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SpeexDSP 初始化失败，AEC/ANS 不可用（AFC 移频仍可用）");
            }
        }
        _echo?.Reset();
        _aecGate = new AecSilenceGate();
        ApplyTuning();
        _estimator.Reset();
        _shifter.Reset();
        _micStitcher.Reset();
        Volatile.Write(ref _chainReady, 1);

        // 5. 计数复位 + 时钟
        Interlocked.Exchange(ref _captureCallbacks, 0);
        Interlocked.Exchange(ref _captureBytes, 0);
        Interlocked.Exchange(ref _renderPulls, 0);
        Interlocked.Exchange(ref _renderBytes, 0);
        Interlocked.Exchange(ref _underruns, 0);
        Interlocked.Exchange(ref _depthSumMsX1000, 0);
        Interlocked.Exchange(ref _depthSamples, 0);
        Interlocked.Exchange(ref _depthMaxMsX1000, 0);
        Interlocked.Exchange(ref _levelX1000, 0);
        Interlocked.Exchange(ref _limiterGainX1000, 1000);
        _clock = Stopwatch.StartNew();
        _statsThrottle.Restart();
        _lastStatsThrottleMs = 0;

        // 6. 渲染：拉取式 IWaveProvider → WasapiOut（事件驱动）
        _source = new PassthroughSource(this, outFormat);
        var output = new WasapiOut(_renderDevice, AudioClientShareMode.Shared, useEventSync: true, _renderLatencyMs);
        output.Init(_source);
        _output = output;
        output.Play();
    }

    private void DetachAndDispose(WasapiCapture capture)
    {
        capture.DataAvailable -= OnCaptureDataAvailable;
        capture.RecordingStopped -= OnCaptureStopped;
        try { capture.StopRecording(); } catch { /* 尚未成功启动，忽略 */ }
        capture.Dispose();
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_state is not (AudioPassthroughState.Running or AudioPassthroughState.Starting))
                return Task.CompletedTask;
            _state = AudioPassthroughState.Stopped;   // 先收态，堵住新数据路径
        }

        CleanupStreams();
        _clock?.Stop();
        RaiseStateChanged();
        StatsUpdated?.Invoke(this, GetStats());
        _logger.LogInformation("扩音直通已停止: {Seconds:F1}s, 欠载 {Underruns}, 丢弃 {Drops}B",
            GetStats().ElapsedSeconds, GetStats().UnderrunEvents, GetStats().DroppedBytes);
        return Task.CompletedTask;
    }

    private void CleanupStreams()
    {
        WasapiCapture? capture;
        WasapiOut? output;
        lock (_gate)
        {
            capture = _capture; _capture = null;
            output = _output; _output = null;
        }

        if (capture != null)
        {
            capture.DataAvailable -= OnCaptureDataAvailable;
            capture.RecordingStopped -= OnCaptureStopped;
            try { capture.StopRecording(); } catch (Exception ex) { _logger.LogWarning(ex, "停止采集异常"); }
        }
        if (output != null)
        {
            try { output.Stop(); } catch (Exception ex) { _logger.LogWarning(ex, "停止渲染异常"); }
        }

        capture?.Dispose();
        output?.Dispose();
        _source = null;
        _ring = null;
        _converter = null;
        Volatile.Write(ref _chainReady, 0);
        _captureDevice?.Dispose(); _captureDevice = null;
        _renderDevice?.Dispose(); _renderDevice = null;
    }

    public void SetAecEnabled(bool enabled)
    {
        var was = Interlocked.Exchange(ref _aecOn, enabled ? 1 : 0);
        if (enabled && was == 0)
        {
            _echo?.Reset();          // 回声路径变过，旧自适应系数作废
            _estimator?.Reset();
            _micStitcher?.Reset();
        }
    }

    public void SetAnsEnabled(bool enabled) => Interlocked.Exchange(ref _ansOn, enabled ? 1 : 0);

    public void SetAfcEnabled(bool enabled)
    {
        var was = Interlocked.Exchange(ref _afcOn, enabled ? 1 : 0);
        if (enabled && was == 0) _shifter?.Reset();
    }

    public void SetAecParams(bool auto, int suppressDb, int suppressActiveDb)
    {
        _aecAuto = auto;
        _aecSuppressDb = Math.Clamp(suppressDb, 10, 60);
        _aecSuppressActiveDb = Math.Clamp(suppressActiveDb, 3, 40);
        ApplyTuning();
    }

    public void SetAnsParams(bool auto, int noiseSuppressDb)
    {
        _ansAuto = auto;
        _ansNoiseSuppressDb = Math.Clamp(noiseSuppressDb, 0, 35);
        ApplyTuning();
    }

    public void SetAecGateEnabled(bool enabled)
    {
        Interlocked.Exchange(ref _aecGateOn, enabled ? 1 : 0);
        _aecGate?.Reset();
    }

    /// <summary>把当前调参状态施加到处理器（自动档=内置推荐值；speex 参数为负 dB）</summary>
    private void ApplyTuning()
    {
        if (_echo == null) return;
        try
        {
            // 自动档：残余压制 40dB / 双讲 15dB / 降噪 15dB（speex 出厂默认量级，实测语音扩音可用）
            var suppress = _aecAuto ? 40 : Math.Clamp(_aecSuppressDb, 10, 60);
            var active = _aecAuto ? 15 : Math.Clamp(_aecSuppressActiveDb, 3, 40);
            var noise = _ansAuto ? 15 : Math.Clamp(_ansNoiseSuppressDb, 0, 35);
            _echo.SetEchoSuppress(suppress, active);
            _echo.SetNoiseSuppress(noise);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "施加调参失败，保持当前参数");
        }
    }

    public void Dispose()
    {
        if (State is AudioPassthroughState.Running or AudioPassthroughState.Starting)
            StopAsync().GetAwaiter().GetResult();
        CleanupStreams();
        _echo?.Dispose();
        _echo = null;
    }

    // === 采集线程 ===

    private void OnCaptureDataAvailable(object? sender, WaveInEventArgs e)
    {
        var converter = _converter;
        var ring = _ring;
        if (converter == null || ring == null || e.BytesRecorded <= 0) return;

        try
        {
            var input = e.Buffer.AsSpan(0, e.BytesRecorded);
            Interlocked.Increment(ref _captureCallbacks);
            Interlocked.Add(ref _captureBytes, e.BytesRecorded);
            UpdateLevel(input);

            if (Volatile.Read(ref _chainReady) != 0)
            {
                ProcessThroughChain(input, ring);
                return;
            }

            var need = converter.MaxOutputBytesFor(e.BytesRecorded);
            if (need > _convertScratch.Length)
                _convertScratch = new byte[(int)BitOperations.RoundUpToPowerOf2((uint)need)];

            var written = converter.Convert(input, _convertScratch.AsSpan());
            if (written > 0)
                ring.Write(_convertScratch.AsSpan(0, written));
        }
        catch (Exception ex)
        {
            // 采集回调里绝不抛：异常会打断 WASAPI 事件线程
            _logger.LogError(ex, "扩音采集回调异常");
        }
    }

    /// <summary>
    /// 处理链路径（采集线程）：16k 规整化 → 凑帧 → 对齐取参考 → AEC/ANS → AFC 移频 → 出格式转换 → 环形缓冲。
    /// 全程预分配零分配；每帧耗时计入统计，超 10ms（帧时长）记为超帧。
    /// </summary>
    private void ProcessThroughChain(ReadOnlySpan<byte> input, PcmRingBuffer ring)
    {
        var need = _micNorm!.MaxOutputBytesFor(input.Length);
        if (need > _micNormScratch.Length)
            _micNormScratch = new byte[(int)BitOperations.RoundUpToPowerOf2((uint)need)];
        var normBytes = _micNorm.Convert(input, _micNormScratch.AsSpan());
        if (normBytes <= 0) return;
        _micStitcher!.Feed(_micNormScratch.AsSpan(0, normBytes));

        var aec = Volatile.Read(ref _aecOn) != 0 && _echo != null;
        var ans = Volatile.Read(ref _ansOn) != 0 && _echo != null;
        var afc = Volatile.Read(ref _afcOn) != 0;
        var gateOn = Volatile.Read(ref _aecGateOn) != 0 && _aecGate != null;

        while (_micStitcher.TryReadFrame(_micFrameBytes))
        {
            _dspClock.Restart();
            Buffer.BlockCopy(_micFrameBytes, 0, _micFrame, 0, _micFrameBytes.Length);

            // 参考对齐取数：标称延迟 + 估计器修正；参考历史不足（启动初期）时旁路 AEC。
            // 条件触发（可选）：参考帧 RMS 静音满保持窗 → 旁路 AEC（判据是参考而非麦克风，见 AecSilenceGate）。
            var lag = _refLagNominal + (_estimator?.OffsetSamples ?? 0);
            var refOk = aec && _refRing!.TryReadAt(_refFrame, lag);
            if (refOk && gateOn && !_aecGate!.Update(Rms(_refFrame)))
            {
                refOk = false;
                Interlocked.Increment(ref _gateBypassFrames);
            }
            if (refOk)
                _echo!.Process(_micFrame, _refFrame, _dspFrame);
            else
            {
                Buffer.BlockCopy(_micFrame, 0, _dspFrame, 0, _micFrameBytes.Length);
                if (ans && _echo != null) _echo.ProcessReferenceOnly(_dspFrame);
            }

            if (afc) _shifter!.Process(_dspFrame);

            // 延迟对齐跟踪（每 ~1s 一次，用标称±10ms 的宽参考窗）
            if (_estimator != null && _refRing!.TryReadAt(_refWide, lag - _estimator.SearchSamples))
                _estimator.Update(_micFrame, _refWide);

            var outBytes = _chainOut!.Convert(MemoryMarshal.AsBytes(_dspFrame.AsSpan()), _chainOutScratch.AsSpan());
            if (outBytes > 0) ring.Write(_chainOutScratch.AsSpan(0, outBytes));

            _dspClock.Stop();
            var ns = (long)(_dspClock.Elapsed.TotalMilliseconds * 1_000_000);
            UpdateMaxDsp(ns);
            if (ns > 10_000_000)   // 帧时长 10ms：处理超帧即实时性受损
                Interlocked.Increment(ref _dspOverruns);
        }
    }

    private void UpdateMaxDsp(long ns)
    {
        long prev = Interlocked.Read(ref _dspMaxNs);
        while (ns > prev && Interlocked.CompareExchange(ref _dspMaxNs, ns, prev) != prev)
            prev = Interlocked.Read(ref _dspMaxNs);
    }

    /// <summary>PCM16 帧 RMS（AEC 条件触发的参考静音判据）</summary>
    private static short Rms(short[] frame)
    {
        long sum = 0;
        for (var i = 0; i < frame.Length; i++)
            sum += frame[i] * frame[i];
        return (short)Math.Clamp(Math.Sqrt((double)sum / frame.Length), 0, short.MaxValue);
    }

    private void OnCaptureStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
            _logger.LogWarning(e.Exception, "扩音采集意外停止");
    }

    private void UpdateLevel(ReadOnlySpan<byte> input)
    {
        float peak = 0f;
        if (IsFloatFormat(_inFormat!))
        {
            foreach (var s in MemoryMarshal.Cast<byte, float>(input))
                peak = Math.Max(peak, MathF.Abs(s));
        }
        else
        {
            foreach (var s in MemoryMarshal.Cast<byte, short>(input))
                peak = Math.Max(peak, MathF.Abs(s / 32768f));
        }
        Interlocked.Exchange(ref _levelX1000, (int)Math.Clamp(peak * 1000f, 0f, 1000f));
    }

    // === 渲染线程 ===

    private int RenderPull(Span<byte> dest)
    {
        var ring = _ring;
        if (ring == null)
        {
            dest.Clear();
            return dest.Length;
        }

        var read = ring.Read(dest);
        if (read < dest.Length)
        {
            dest[read..].Clear();
            Interlocked.Increment(ref _underruns);
        }
        Interlocked.Add(ref _renderBytes, dest.Length);
        Interlocked.Increment(ref _renderPulls);

        // 音量 + 限制器（渲染格式 float32 / PCM16；SoftLimiter 天花板 0.95 防破音）
        var limiter = _limiter;
        if (dest.Length > 0 && limiter != null)
        {
            var vol = _volume;
            if (_fadeInLeft > 0)
            {
                var bytesPerFrame = _outFormat!.BitsPerSample / 8 * _outFormat.Channels;
                vol *= 1f - _fadeInLeft / _fadeInTotal;
                _fadeInLeft = Math.Max(0, _fadeInLeft - dest.Length / bytesPerFrame);
            }
            if (IsFloatFormat(_outFormat!))
            {
                var f = MemoryMarshal.Cast<byte, float>(dest);
                if (vol != 1.0f)
                    foreach (ref var s in f) s *= vol;
                limiter.Process(f);
            }
            else
            {
                var n = dest.Length / 2;
                if (_limitScratch.Length < n)
                    _limitScratch = new float[BitOperations.RoundUpToPowerOf2((uint)n)];
                var shorts = MemoryMarshal.Cast<byte, short>(dest);
                for (var i = 0; i < n; i++)
                    _limitScratch[i] = shorts[i] / 32768f * vol;
                limiter.Process(_limitScratch.AsSpan(0, n));
                for (var i = 0; i < n; i++)
                    shorts[i] = (short)MathF.Round(Math.Clamp(_limitScratch[i], -1f, 1f) * 32767f);
            }
            Interlocked.Exchange(ref _limiterGainX1000, (int)Math.Clamp(limiter.CurrentGain * 1000f, 0f, 1000f));
        }

        // 参考信号捕获（AEC 参考链）：实际送出的声音（音量+限制器之后）→ 16k 单声道 → 延迟环
        if (Volatile.Read(ref _chainReady) != 0)
        {
            try
            {
                var refBytes = _refDown!.Convert(dest, _refDownScratch);
                if (refBytes > 0)
                    _refRing!.Write(MemoryMarshal.Cast<byte, short>(_refDownScratch.AsSpan(0, refBytes)));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "参考信号捕获异常");
            }
        }

        // 深度统计（ms，按渲染格式折算）
        var bytesPerMs = _outFormat!.AverageBytesPerSecond / 1000.0;
        var depthMsX1000 = (long)(ring.Count / bytesPerMs * 1000);
        Interlocked.Add(ref _depthSumMsX1000, depthMsX1000);
        Interlocked.Increment(ref _depthSamples);
        Interlocked.Exchange(ref _depthMaxMsX1000, Math.Max(depthMsX1000, Interlocked.Read(ref _depthMaxMsX1000)));

        MaybeRaiseStats();
        return dest.Length;
    }

    private void MaybeRaiseStats()
    {
        var now = _statsThrottle.ElapsedMilliseconds;
        var last = Interlocked.Read(ref _lastStatsThrottleMs);
        if (now - last < 200) return;
        if (Interlocked.CompareExchange(ref _lastStatsThrottleMs, now, last) != last) return;
        StatsUpdated?.Invoke(this, GetStats());
    }

    public AudioPassthroughStats GetStats()
    {
        var outFormat = _outFormat;
        var inFormat = _inFormat;
        var depthSamples = Interlocked.Read(ref _depthSamples);
        return new AudioPassthroughStats
        {
            State = State,
            CaptureDevice = _captureName,
            RenderDevice = _renderName,
            CaptureFormat = inFormat == null ? string.Empty : $"{inFormat.SampleRate}Hz/{inFormat.BitsPerSample}bit/{inFormat.Channels}ch",
            RenderFormat = outFormat == null ? string.Empty : $"{outFormat.SampleRate}Hz/{outFormat.BitsPerSample}bit/{outFormat.Channels}ch",
            ElapsedSeconds = _clock?.Elapsed.TotalSeconds ?? 0,
            CaptureCallbacks = Interlocked.Read(ref _captureCallbacks),
            CaptureBytes = Interlocked.Read(ref _captureBytes),
            RenderPulls = Interlocked.Read(ref _renderPulls),
            RenderBytes = Interlocked.Read(ref _renderBytes),
            RingDepthMs = outFormat == null ? 0 : CurrentRingDepthMs(),
            RingDepthAvgMs = depthSamples == 0 ? 0 : Interlocked.Read(ref _depthSumMsX1000) / 1000.0 / depthSamples,
            RingDepthMaxMs = Interlocked.Read(ref _depthMaxMsX1000) / 1000.0,
            DroppedBytes = _ring?.DroppedBytes ?? 0,
            UnderrunEvents = Interlocked.Read(ref _underruns),
            Level = Volatile.Read(ref _levelX1000) / 1000f,
            RenderLatencyMs = _renderLatencyMs,
            Volume = _volume,
            LimiterGain = Volatile.Read(ref _limiterGainX1000) / 1000f,
            AecOffsetMs = _estimator == null ? 0 : _estimator.OffsetSamples / 16.0,
            DspMaxMs = Interlocked.Read(ref _dspMaxNs) / 1_000_000.0,
            DspOverruns = Interlocked.Read(ref _dspOverruns),
            EstimatedLatencyMs = EstimateBoxLatencyMs(),
            AecGateBypassMs = Interlocked.Read(ref _gateBypassFrames) * 10
        };
    }

    /// <summary>
    /// Box 段估计延迟（USB 音频到达 → 扬声器发声），组合可加性拆解：
    /// 采集引擎周期(≈10ms) + 处理链附加(凑帧 10ms + AFC 群延迟 1ms，全关为 0)
    /// + 环形深度(活值) + 渲染缓冲(设定值)。
    /// </summary>
    private double EstimateBoxLatencyMs()
    {
        var chainActive = Volatile.Read(ref _chainReady) != 0 &&
                          (Volatile.Read(ref _aecOn) != 0 || Volatile.Read(ref _ansOn) != 0 || Volatile.Read(ref _afcOn) != 0);
        var chainMs = chainActive ? 10.0 + (Volatile.Read(ref _afcOn) != 0 ? 1.0 : 0.0) : 0.0;
        return 10.0 + chainMs + CurrentRingDepthMs() + _renderLatencyMs;
    }

    private double CurrentRingDepthMs()
    {
        var ring = _ring;
        var outFormat = _outFormat;
        if (ring == null || outFormat == null) return 0;        return ring.Count / (outFormat.AverageBytesPerSecond / 1000.0);
    }

    private void RaiseStateChanged()
    {
        StateChanged?.Invoke(this, State);
    }

    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00AA00389B71");

    /// <summary>
    /// 判定是否 IEEE float 格式。WASAPI 混音格式常为 WAVE_FORMAT_EXTENSIBLE 包裹的 float，
    /// 须解包 SubFormat 再判（实机：EXTENSIBLE IEEE_FLOAT 48k/2ch/32bit）。
    /// </summary>
    private static bool IsFloatFormat(WaveFormat format) =>
        format.Encoding == WaveFormatEncoding.IeeeFloat ||
        (format is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubFormat);

    private static MMDevice ResolveDevice(MMDeviceEnumerator enumerator, DataFlow flow, string? deviceId, string label)
    {
        if (string.IsNullOrWhiteSpace(deviceId) ||
            deviceId.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
        }

        // 精确 ID 优先
        try
        {
            return enumerator.GetDevice(deviceId);
        }
        catch (Exception)
        {
            // fallthrough：按友好名称子串匹配
        }

        var match = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)
            .FirstOrDefault(d => d.FriendlyName.Contains(deviceId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"未找到匹配的{label}设备: {deviceId}");
        return match;
    }

    /// <summary>渲染拉取源：WasapiOut 事件线程回调进入引擎</summary>
    private sealed class PassthroughSource : IWaveProvider
    {
        private readonly WasapiPassthroughEngine _engine;

        public WaveFormat WaveFormat { get; }

        public PassthroughSource(WasapiPassthroughEngine engine, WaveFormat format)
        {
            _engine = engine;
            WaveFormat = format;
        }

        public int Read(byte[] buffer, int offset, int count)
            => _engine.RenderPull(buffer.AsSpan(offset, count));
    }
}
