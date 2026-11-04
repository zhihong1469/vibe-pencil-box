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
/// WASAPI 扩音直通引擎：采集（事件驱动；一键独占=ExclusiveCapturePump 优先，失败自动回退共享级联）→ 零前视格式转换 → 环形缓冲（丢最旧）
/// → 渲染拉取（事件驱动，欠载补静音）。与录音路径（NAudioAudioCapture）完全独立。
/// 附加延迟 ≈ 采集引擎周期(~10ms) + 环形深度(RingDepthMs，活量) + 渲染缓冲(RenderLatencyMs)。
/// </summary>
public sealed class WasapiPassthroughEngine : IAudioPassthrough
{
    private readonly ILogger<WasapiPassthroughEngine> _logger;

    private readonly object _gate = new();

    private WasapiCapture? _capture;
    private ExclusiveCapturePump? _exclusivePump;   // 一键独占采集（2026-11-05）：成位时替代 _capture
    private bool _captureExclusive;                 // 本次会话实际是否独占
    private string _captureModeNote = string.Empty; // 模式说明/回退原因（对账统计 CaptureModeNote）
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

    // 带通降噪（AEC 前级）：HPF/LPF 各两节级联 24dB/oct；数组整体替换保证运行中调频无撕裂
    private volatile BiquadFilter[]? _bandpass;
    // 滤波链状态（SetBandpass/SetNotches 与 StartCore 共用一份，整体重建数组）
    private int _hpfHzState = 80;
    private int _lpfHzState;
    private int[] _notchHzState = [];
    private int _notchQState = 25;

    // 桥接副汇：处理链后级信号复投第二渲染设备（虚拟声卡/第二扬声器），供其他软件当已降噪麦克风。
    // 写满丢最旧、绝不反压主链——主扩音延迟不受它影响；消费端卡顿只体现为 BridgeDroppedBytes。
    private PcmRingBuffer? _bridgeRing;
    private WasapiOut? _bridgeOut;
    private string _bridgeName = string.Empty;
    private SoftLimiter? _limiter;
    private float[] _limitScratch = Array.Empty<float>();
    private Stopwatch? _clock;

    private AudioPassthroughState _state = AudioPassthroughState.Idle;

    // 外源 PCM 泵模式（ExternalSource=true 时置位）：无 WASAPI 采集，PCM 经 WriteExternalPcm 进链。
    // 仅 StartCore 同步置位、清理时复位，跨线程读可见性靠"先置位后开流"的顺序保证。
    private bool _externalSource;

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
    private const int ChainSearchSamples = 320;  // 对齐搜索半径 20ms（板载回环实测 ±10ms 会被稳态噪声钉死在边界）
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
        // 1. 解析设备（外源模式无本地采集设备，PCM 由 WriteExternalPcm 泵入）
        using var enumerator = new MMDeviceEnumerator();
        _externalSource = options.ExternalSource;   // 每次启动重置（防上次外源残留）
        if (options.ExternalSource)
        {
            _captureDevice = null;
            _captureName = "（外源 PCM 泵）";
        }
        else
        {
            _captureDevice = ResolveDevice(enumerator, DataFlow.Capture, options.CaptureDeviceId, "录音");
            _captureName = _captureDevice.FriendlyName;
        }
        _renderDevice = ResolveDevice(enumerator, DataFlow.Render, options.RenderDeviceId, "播放");
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

        // 带通降噪下/上边（0=关）+ 定点滤除（notch，中心频率列表）
        _notchHzState = options.NotchCentersHz ?? [];
        _notchQState = options.NotchQ;
        ApplyBandpass(options.HpfHz, options.LpfHz);

        // 2.5 桥接副汇（可选）：必须在采集启动之前初始化——采集一开跑就持续进环，
        // 桥的 WasapiOut 构造/Init 若插在采集与渲染之间，耗时会把主环垫高几十毫秒（直接吃进主链延迟）。
        // 复用主链输出字节流；混音格式必须与主输出一致（共享模式端点几乎都是 48k/32f）
        if (!string.IsNullOrWhiteSpace(options.BridgeDeviceId))
        {
            using var bridgeEnum = new MMDeviceEnumerator();
            var bridgeDevice = ResolveDevice(bridgeEnum, DataFlow.Render, options.BridgeDeviceId, "桥接");
            using (var bc = bridgeDevice.AudioClient)
                if (!bc.MixFormat.Equals(outFormat))
                    throw new NotSupportedException($"桥接端点混音格式与主输出不一致（{bc.MixFormat} ≠ {outFormat}），快线桥接暂不支持");
            _bridgeRing = new PcmRingBuffer(Math.Max(1, (int)(outFormat.AverageBytesPerSecond * 0.5)));
            _bridgeName = bridgeDevice.FriendlyName;
            var bridgeLatency = Math.Clamp(options.BridgeLatencyMs, 50, 500);
            _bridgeOut = new WasapiOut(bridgeDevice, AudioClientShareMode.Shared, useEventSync: true, bridgeLatency);
            _bridgeOut.Init(new BridgeSource(this, outFormat));
            _bridgeOut.Play();
            _logger.LogInformation("桥接副汇已启动: {Device}, 缓冲 {Ms}ms", _bridgeName, bridgeLatency);
        }

        // 3. 采集：一键独占优先（2026-11-05），失败自动回退共享级联——独占不可用是常态回退不是错误
        //（独占=独占端点，与同端点任何流互斥；UAC 麦原生 16k 时独占可绕开混音器吃掉一段引擎税）。
        // 3b. 共享级联尝试（请求格式 → 采集端混音格式 × 事件同步 → 睡眠同步）。
        // 实机坑：USB Audio 1.0 + 事件同步 + 非混音格式 → AudioClient.Initialize 阻塞 ~30s 后
        // E_UNEXPECTED(0x8000FFFF)；一旦出现该码，跳过剩余事件同步尝试，直落睡眠同步。
        // 外源模式整段跳过：采集格式固定 16k/16bit/1ch（mSBC 解码定参，泵入方保证）。
        WaveFormat inFormat;
        _captureExclusive = false;
        _captureModeNote = string.Empty;
        if (options.ExternalSource)
        {
            inFormat = new WaveFormat(16000, 16, 1);
            _logger.LogInformation("外源 PCM 泵模式：跳过 WASAPI 采集（16k/16bit/1ch 由泵入方保证）");
        }
        else
        {
        var requested = new WaveFormat(Math.Clamp(options.SampleRate, 8000, 96000), 16, Math.Clamp(options.Channels, 1, 2));
        WaveFormat captureMix;
        using (var captureClient = _captureDevice!.AudioClient)
        {
            captureMix = captureClient.MixFormat;
        }

        inFormat = requested;

        // 3a. 一键独占（2026-11-05）：协商+起泵；任何失败只记因不抛——回退下方共享级联。
        // 独占=独占端点：与同端点任何流互斥（含本引擎自己的另一条流），被占是常态回退不是错误。
        if (options.ExclusiveCapture)
            TryStartExclusiveCapture(_captureDevice!, requested);

        if (!_captureExclusive)
        {
        WasapiCapture? capture = null;
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
                $"采集流打开失败（已尝试 独占 + 请求格式/混音格式 × 事件/睡眠同步）: {_captureName}", lastError);

        _capture = capture;
        }
        }

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
        _estimator = new EchoDelayEstimator(ChainFrameSamples, 16000, searchMs: 20, updateEveryFrames: 100);
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

    /// <summary>按当前状态重建滤波链：HPF/LPF 各两节（24dB/oct）+ 每个 notch 中心一节</summary>
    private void ApplyFilterChain()
    {
        var rate = _outFormat?.SampleRate ?? 16000;
        var list = new List<BiquadFilter>(4 + _notchHzState.Length);
        if (_hpfHzState > 0)
        {
            list.Add(new BiquadFilter(BiquadResponse.HighPass, _hpfHzState, rate));
            list.Add(new BiquadFilter(BiquadResponse.HighPass, _hpfHzState, rate));
        }
        if (_lpfHzState > 0)
        {
            list.Add(new BiquadFilter(BiquadResponse.LowPass, _lpfHzState, rate));
            list.Add(new BiquadFilter(BiquadResponse.LowPass, _lpfHzState, rate));
        }
        foreach (var hz in _notchHzState)
        {
            if (hz > 0 && hz < rate / 2 * 0.99)
                list.Add(new BiquadFilter(BiquadResponse.Notch, hz, rate, Math.Max(1, _notchQState)));
        }
        Volatile.Write(ref _bandpass, list.Count == 0 ? null : list.ToArray());
    }

    /// <summary>设置带通（UI 滑条/CLI 起动参数）并重建滤波链</summary>
    private void ApplyBandpass(int hpfHz, int lpfHz)
    {
        _hpfHzState = Math.Max(0, hpfHz);
        _lpfHzState = Math.Max(0, lpfHz);
        ApplyFilterChain();
    }

    /// <summary>运行中调整带通（UI 滑条；数组整体替换，采集线程逐帧取引用）</summary>
    public void SetBandpass(int hpfHz, int lpfHz) => ApplyBandpass(hpfHz, lpfHz);

    /// <summary>运行中调整定点滤除（notch；数组整体替换，采集线程逐帧取引用）</summary>
    public void SetNotches(int[] centersHz, int q)
    {
        _notchHzState = centersHz ?? [];
        _notchQState = q;
        ApplyFilterChain();
    }

    /// <summary>
    /// 一键独占尝试（2026-11-05）：候选协商（请求格式优先+t7 回退表去重）→ 起独占泵。
    /// 任何失败只记 <c>_captureModeNote</c> 不抛——调用方按 <c>_captureExclusive</c> 决定是否走共享级联。
    /// 探测/初始化被占（0x8889000A）=常态回退；协商成功但 Initialize 被拒=设备态竞态，同样回退。
    /// </summary>
    private void TryStartExclusiveCapture(MMDevice device, WaveFormat requested)
    {
        var busy = false;
        var chosen = ExclusiveCapturePump.SelectFormat(requested, ExclusiveCaptureEngine.CandidateFormats, f =>
        {
            try
            {
                using var probe = device.AudioClient;
                return probe.IsFormatSupported(AudioClientShareMode.Exclusive, f);
            }
            catch (COMException cex)
            {
                if (cex.HResult == ExclusiveCapturePump.AudclntEDeviceInUse)
                    busy = true;
                return false;   // 探测口失败：该格式视为不可用；被占归因走下面统一口径
            }
        }, out var reason);
        if (busy)
            reason = "端点被占用（独占与同端点任何流互斥）——先停掉其他用麦的进程/流";
        if (chosen == null)
        {
            _captureModeNote = $"共享（独占回退：{reason}）";
            _logger.LogWarning("独占采集不可用：{Reason}，回退共享级联", reason);
            return;
        }

        var pump = new ExclusiveCapturePump();
        pump.DataAvailable += OnCaptureDataAvailable;
        pump.RecordingStopped += OnCaptureStopped;
        try
        {
            pump.Start(device, chosen);
            _exclusivePump = pump;
            _captureExclusive = true;
            _captureModeNote = "独占";
            _inFormat = chosen;
            _logger.LogInformation("独占采集成位: {Format}（绕开混音器，采集周期 ~10ms）", chosen);
        }
        catch (Exception ex)
        {
            pump.DataAvailable -= OnCaptureDataAvailable;
            pump.RecordingStopped -= OnCaptureStopped;
            pump.Dispose();
            _captureModeNote = $"共享（独占回退：{ExclusiveCapturePump.DescribeExclusiveFailure(ex)}）";
            _logger.LogWarning(ex, "独占采集初始化失败（协商已成、启动被拒），回退共享级联");
        }
    }

    /// <summary>
    /// 启动失败候选的收尾（非阻塞）：事件先解挂，Stop/Dispose 踢后台。
    /// NAudio StopRecording 内部 Join 采集线程；候选若已 StartRecording，其线程可能睡在
    /// 设备事件 WaitOne(∞)（管道型虚拟设备无进料不发事件，11-03 真机死机实证）——同步收尾=UI 卡死。
    /// </summary>
    private void DetachAndDispose(WasapiCapture candidate)
    {
        candidate.DataAvailable -= OnCaptureDataAvailable;
        candidate.RecordingStopped -= OnCaptureStopped;
        _ = Task.Run(() =>
        {
            try { candidate.StopRecording(); } catch { /* 尚未成功启动，忽略 */ }
            try { candidate.Dispose(); } catch { /* 已停则忽略 */ }
        });
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

    /// <summary>
    /// 停止收尾（非阻塞）：门内一次性快照全部句柄并同步复位字段（新 Start 立刻安全），
    /// 阻塞段（Stop/Dispose 内部 Join 事件线程）整体踢后台。UI 线程零等待——
    /// 管道型虚拟设备（CABLE Output）无进料不发事件，采集线程睡在 WaitOne(∞)，
    /// 同步 Join=永久卡死（11-03 真机死机实证；与串口同步关句柄坑同族：阻塞收尾必须有后台闸门）。
    /// </summary>
    private void CleanupStreams()
    {
        WasapiCapture? capture;
        ExclusiveCapturePump? pump;
        WasapiOut? output;
        WasapiOut? bridgeOut;
        MMDevice? captureDevice;
        MMDevice? renderDevice;
        lock (_gate)
        {
            capture = _capture; _capture = null;
            pump = _exclusivePump; _exclusivePump = null;
            output = _output; _output = null;
            bridgeOut = _bridgeOut; _bridgeOut = null;
            _bridgeRing = null;
            _bridgeName = string.Empty;
            _source = null;
            _ring = null;
            _converter = null;
            Volatile.Write(ref _chainReady, 0);
            captureDevice = _captureDevice; _captureDevice = null;
            renderDevice = _renderDevice; _renderDevice = null;
        }

        if (capture == null && pump == null && output == null && bridgeOut == null) return;

        _ = Task.Run(() =>
        {
            try
            {
                if (pump != null)
                {
                    pump.DataAvailable -= OnCaptureDataAvailable;
                    pump.RecordingStopped -= OnCaptureStopped;
                    try { pump.Stop(); } catch (Exception ex) { _logger.LogWarning(ex, "停止独占采集异常"); }
                    pump.Dispose();
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
                if (bridgeOut != null)
                {
                    try { bridgeOut.Stop(); } catch (Exception ex) { _logger.LogWarning(ex, "停止桥接渲染异常"); }
                    bridgeOut.Dispose();
                }
                captureDevice?.Dispose();
                renderDevice?.Dispose();
                _logger.LogInformation("扩音停止后台清理完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "扩音停止后台清理异常");
            }
        });
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

    /// <summary>软件增益运行中即调即生效（渲染拉取线程每次读 _volume；float 写原子，限制器兜破音）。</summary>
    public void SetVolume(float volume)
        => _volume = Math.Clamp(volume, 0f, PassthroughStartOptions.MaxVolume);

    public void SetAnsEnabled(bool enabled)
    {
        Interlocked.Exchange(ref _ansOn, enabled ? 1 : 0);
        // AEC 主路径的 NS 在 Process 内部无条件跑，开关必须落到预处理的 DENOISE 位才生效
        try { _echo?.SetDenoiseEnabled(enabled); }
        catch (Exception ex) { _logger.LogWarning(ex, "切换 DENOISE 失败"); }
    }

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
            _echo.SetDenoiseEnabled(Volatile.Read(ref _ansOn) != 0);
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

    /// <summary>
    /// 外源 PCM 泵（<see cref="PassthroughStartOptions.ExternalSource"/>=true）：16k/16bit/单声道 PCM
    /// 直入处理链 → 环形缓冲 → 渲染。与采集回调同语义：未运行/链未就绪丢弃，异常吞掉不打断泵入方
    /// （BLE 通知回调线程同此要求——异常不得打断 WinRT 线程池）。
    /// </summary>
    public int WriteExternalPcm(ReadOnlySpan<byte> pcm)
    {
        var ring = _ring;
        if (pcm.IsEmpty || ring == null || !_externalSource ||
            Volatile.Read(ref _chainReady) == 0 || State != AudioPassthroughState.Running)
            return 0;

        try
        {
            Interlocked.Increment(ref _captureCallbacks);
            Interlocked.Add(ref _captureBytes, pcm.Length);
            UpdateLevel(pcm);
            ProcessThroughChain(pcm, ring);
            return pcm.Length;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "外源 PCM 泵异常");
            return 0;
        }
    }

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

            // 带通降噪（AEC 前级）：切机械抖动/低频传导与高频嘶声；参考同链已滤，路径一致性由自适应保证
            var bp = _bandpass;
            if (bp != null)
                foreach (var section in bp) section.Process(_micFrame);

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
            if (outBytes > 0)
            {
                ring.Write(_chainOutScratch.AsSpan(0, outBytes));
                _bridgeRing?.Write(_chainOutScratch.AsSpan(0, outBytes));   // 满则丢最旧，不反压
            }

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
            BridgeDevice = _bridgeName,
            BridgeDroppedBytes = _bridgeRing?.DroppedBytes ?? 0,
            NotchConfig = _notchHzState.Length == 0 ? string.Empty
                : $"{string.Join(",", _notchHzState)}Hz Q{_notchQState}",
            EstimatedLatencyMs = EstimateBoxLatencyMs(),
            AecGateBypassMs = Interlocked.Read(ref _gateBypassFrames) * 10,
            CaptureExclusive = _captureExclusive,
            CaptureModeNote = _captureModeNote
        };
    }

    /// <summary>
    /// Box 段估计延迟（USB 音频到达 → 扬声器发声），组合可加性拆解：
    /// 采集引擎周期(≈10ms；外源泵 0——BLE 通知到达即泵入) + 处理链附加(凑帧 10ms + AFC 群延迟 1ms，全关为 0)
    /// + 环形深度(活值) + 渲染缓冲(设定值)。
    /// </summary>
    private double EstimateBoxLatencyMs()
    {
        var chainActive = Volatile.Read(ref _chainReady) != 0 &&
                          (Volatile.Read(ref _aecOn) != 0 || Volatile.Read(ref _ansOn) != 0 || Volatile.Read(ref _afcOn) != 0);
        var chainMs = chainActive ? 10.0 + (Volatile.Read(ref _afcOn) != 0 ? 1.0 : 0.0) : 0.0;
        var captureMs = _externalSource ? 0.0 : 10.0;
        return captureMs + chainMs + CurrentRingDepthMs() + _renderLatencyMs;
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

    /// <summary>桥接副汇拉取源：读桥接环，欠载补静音（非实时消费，宁缺毋滞）</summary>
    private sealed class BridgeSource : IWaveProvider
    {
        private readonly WasapiPassthroughEngine _engine;

        public WaveFormat WaveFormat { get; }

        public BridgeSource(WasapiPassthroughEngine engine, WaveFormat format)
        {
            _engine = engine;
            WaveFormat = format;
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            var n = _engine._bridgeRing?.Read(buffer.AsSpan(offset, count)) ?? 0;
            if (n < count) buffer.AsSpan(offset + n, count - n).Clear();
            return count;
        }
    }
}
