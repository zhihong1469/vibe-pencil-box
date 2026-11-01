// <copyright file="SoftReceiverViewModel.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using PencilBox.Core.Ble;
using PencilBox.Core.Ble.SoftReceiver;
using PencilBox.Core.Communication;
using PencilBox.Core.Models;
using PencilBox.Core.Protocol.Company;
using PencilBox.Core.Services;
using PencilBox.Desktop.Helpers;
using PencilBox.Infrastructure.Audio;

namespace PencilBox.Desktop.ViewModels;

/// <summary>
/// 软件接收器页（形态③：PC 直连键盘收语音，绕过硬件基站）。
/// 状态卡（连接/电量/充电/档位）+ 事件流（键/档位/槽/seq 跳）+ 扩音泵出开关。
/// 会话编排/解析/解码在 Core（<see cref="SoftReceiverSession"/>），本 VM 只做编排与展示；
/// 扩音泵复用 <see cref="IAudioPassthrough"/> 外源模式（ExternalSource，跳过 WASAPI 采集）。
/// 机制文档 = _share/box/docs/GitFlow/feature/soft-receiver/README.md。
/// </summary>
public partial class SoftReceiverViewModel : ObservableObject
{
    private const int EventLogCapacity = 200;
    private const string PenPrefix = BleNameConstants.PenPrefix;

    private readonly IBleCentral _central;
    private readonly IBleScanner _scanner;
    private readonly IAudioPassthrough _passthrough;

    private SoftReceiverSession? _session;
    private readonly Stopwatch _speakerClock = new();
    private DispatcherTimer? _speakerStatsTimer;

    public SoftReceiverViewModel(IBleCentral central, IBleScanner scanner, IAudioPassthrough passthrough)
    {
        _central = central;
        _scanner = scanner;
        _passthrough = passthrough;
    }

    // ---- 连接状态卡 ----
    [NotifyPropertyChangedFor(nameof(CanProbe))]
    [NotifyPropertyChangedFor(nameof(CanToggleSpeaker))]
    [NotifyPropertyChangedFor(nameof(CanToggleBridge))]
    [ObservableProperty] private bool _isBusy;
    [NotifyPropertyChangedFor(nameof(CanProbe))]
    [NotifyPropertyChangedFor(nameof(CanToggleSpeaker))]
    [NotifyPropertyChangedFor(nameof(CanToggleBridge))]
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _connectionText = "未连接";
    [ObservableProperty] private string _macInput = "";
    [ObservableProperty] private string _batteryText = "—";
    [ObservableProperty] private string _chargeText = "—";
    [ObservableProperty] private string _devStateText = "—";
    [ObservableProperty] private string _modeText = "—";
    [ObservableProperty] private string _statsText = "—";
    [NotifyPropertyChangedFor(nameof(CanToggleSpeaker))]
    [NotifyPropertyChangedFor(nameof(SpeakerButtonText))]
    [ObservableProperty] private bool _isSpeakerOn;
    [ObservableProperty] private string _speakerText = "关";

    // ---- 虚拟麦克风桥（R1PBleAudio 桥式腿，复用 SrxBridgePump） ----
    [NotifyPropertyChangedFor(nameof(CanToggleBridge))]
    [ObservableProperty] private bool _isBridgeOn;
    [ObservableProperty] private string _bridgeText = "关";
    [ObservableProperty] private string _bridgeDeviceText = "—";
    [ObservableProperty] private string _bridgeInfoText = "—";

    // ---- 收流录制 WAV（receive 腿） ----
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private string _recordText = "录制 WAV";

    // ---- 延迟实测（chirp 回环，复用 SoftReceiverProbe） ----
    [NotifyPropertyChangedFor(nameof(CanProbe))]
    [NotifyPropertyChangedFor(nameof(CanToggleSpeaker))]
    [NotifyPropertyChangedFor(nameof(CanToggleBridge))]
    [ObservableProperty] private bool _isProbing;
    [ObservableProperty] private string _probeSecondsInput = "30";
    [ObservableProperty] private string _probeResultText = "（未测）";

    private CancellationTokenSource? _probeCts;
    private SrxBridgePump? _bridge;
    private WaveFileWriter? _recordWriter;
    private readonly object _recordLock = new();
    private long _recordFrames;
    private Stopwatch? _recordSw;

    /// <summary>「开始延迟实测」可用 = 已连接且未在测未忙（按钮 IsEnabled 直绑，勿用转换器）</summary>
    public bool CanProbe => IsConnected && !IsProbing && !IsBusy;

    /// <summary>「扩音泵出」按钮可用 = 已连接，或扩音正开着（允许随时关）；实测中锁死</summary>
    public bool CanToggleSpeaker => (IsConnected || IsSpeakerOn) && !IsProbing;

    /// <summary>扩音按钮文案随态翻转</summary>
    public string SpeakerButtonText => IsSpeakerOn ? "关扩音" : "开扩音";

    /// <summary>「虚拟麦克风桥」按钮可用 = 已连接，或桥正开着（允许随时关）；实测中锁死</summary>
    public bool CanToggleBridge => (IsConnected || IsBridgeOn) && !IsProbing && !IsBusy;

    /// <summary>事件流（最新在下；容量裁剪防长会话膨胀）</summary>
    public ObservableCollection<string> EventLog { get; } = [];

    private SoftReceiverSession? Session
    {
        get => _session;
        set
        {
            if (_session != null)
            {
                _session.AudioFrameReady -= OnAudioFrameReady;
                _session.KeyEvent -= OnKeyEvent;
                _session.SwitchChanged -= OnSwitchChanged;
                _session.SlotChanged -= OnSlotChanged;
                _session.ResponseReceived -= OnResponseReceived;
                _session.DataFrameParsed -= OnDataFrameParsed;
                _session.AudioSeqJump -= OnSeqJump;
                _session.Diagnostics -= OnDiagnostics;
            }
            _session = value;
            if (_session != null)
            {
                _session.AudioFrameReady += OnAudioFrameReady;
                _session.KeyEvent += OnKeyEvent;
                _session.SwitchChanged += OnSwitchChanged;
                _session.SlotChanged += OnSlotChanged;
                _session.ResponseReceived += OnResponseReceived;
                _session.DataFrameParsed += OnDataFrameParsed;
                _session.AudioSeqJump += OnSeqJump;
                _session.Diagnostics += OnDiagnostics;
            }
        }
    }

    /// <summary>扫描并连接（5s 广播扫 SunVote-R1 前缀；填了 MAC 输入框则直连）。</summary>
    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (IsBusy || IsConnected) return;
        IsBusy = true;
        try
        {
            string? mac = NormalizeMac(MacInput);
            if (mac == null)
            {
                Log("扫描 5s 找笔端广播（或直接填 MAC 直连）…");
                var devices = await _scanner.ScanAsync(5);
                mac = devices
                    .Where(d => (d.LocalName ?? "").StartsWith(PenPrefix, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(d => d.Rssi)
                    .FirstOrDefault()?.MacDisplayText;
                if (mac == null)
                {
                    Log("未发现笔端广播：键盘连着基站时不广播；填 MAC 直连，或让键盘脱离基站/进配对态。");
                    return;
                }
            }

            var session = new SoftReceiverSession(_central);
            Session = session;
            await session.ConnectAsync(mac);
            IsConnected = true;
            ConnectionText = $"已连接 {mac}";
            Log($"svr1 特征：DATA={(session.DataCharacteristicFound ? "✓" : "✗")} " +
                $"CMD={(session.CmdCharacteristicFound ? "✓" : "✗")} " +
                $"RSP={(session.RspCharacteristicFound ? "✓" : "✗")}");

            var cts = new CancellationTokenSource();
            _subscribeCts = cts;
            _ = Task.Run(async () =>
            {
                try
                {
                    await session.StartAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // 正常收尾
                }
                catch (Exception ex)
                {
                    Log($"收流异常：{ex.Message}");
                }
            });
            Log("收流中…（语音流触发=物理 K2 语音键 或 S1 扩音档）");
        }
        catch (Exception ex)
        {
            Log($"连接失败：{ex.Message}");
            Session = null;
            IsConnected = false;
            ConnectionText = "未连接";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private CancellationTokenSource? _subscribeCts;

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (!IsConnected && Session == null) return;
        IsBusy = true;
        try
        {
            if (_probeCts != null)
            {
                // 实测中直接断开：取消等待窗，probe 任务自行收尾（停播/分析/清场）
                _probeCts.Cancel();
                _probeCts = null;
            }
            if (_subscribeCts != null)
            {
                _subscribeCts.Cancel();
                _subscribeCts.Dispose();
                _subscribeCts = null;
            }
            if (Session != null)
                await Session.StopAsync();
            await _central.DisconnectAsync();
        }
        catch (Exception ex)
        {
            Log($"断开异常：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
            IsConnected = false;
            ConnectionText = "未连接";
            BatteryText = ChargeText = DevStateText = ModeText = "—";
            StatsText = "—";
            if (IsSpeakerOn)
                ToggleSpeakerExecute();
            if (IsBridgeOn)
                StopBridge("断开");
            if (IsRecording)
                StopRecord();
            _speakerStatsTimer?.Stop();
        }
    }

    /// <summary>扩音泵出开关：解码 PCM 直入扩音引擎外源模式（替代 WASAPI 采集）→ 扬声器。</summary>
    [RelayCommand]
    private void ToggleSpeaker()
    {
        if (IsProbing)
        {
            Log("[延迟实测] 进行中，不能开关扬声器（等实测结束或断开）");
            return;
        }
        if (IsSpeakerOn)
        {
            ToggleSpeakerExecute();
            return;
        }
        if (!IsConnected)
        {
            Log("先连接键盘再开扬声器");
            return;
        }
        ToggleSpeakerExecute();
    }

    private void ToggleSpeakerExecute()
    {
        if (IsSpeakerOn)
        {
            _speakerClock.Reset();
            _ = _passthrough.StopAsync();
            IsSpeakerOn = false;
            SpeakerText = "关";
            Log("扩音泵出已关");
            MaybeStopStatsTimer();
            return;
        }
        try
        {
            _ = _passthrough.StartAsync(new PassthroughStartOptions
            {
                ExternalSource = true,
                RenderLatencyMs = 40,
                Volume = 1.0f,
                EnableAec = true,
                EnableAns = true,
                EnableAfc = false,
                HpfHz = 80,
            });
            IsSpeakerOn = true;
            SpeakerText = "开（外源泵 16k）";
            _speakerClock.Restart();
            EnsureStatsTimer();
            Log("扩音泵出已开（16k 外源泵→处理链→扬声器）");
        }
        catch (Exception ex)
        {
            Log($"扩音启动失败：{ex.Message}");
        }
    }

    private DispatcherTimer CreateSpeakerStatsTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            var s = _passthrough.GetStats();
            StatsText = $"泵出运行 {_speakerClock.Elapsed.TotalSeconds:F0}s：" +
                        $"环深 {s.RingDepthMs:F0}ms / 估计延迟 {s.EstimatedLatencyMs:F0}ms / 欠载 {s.UnderrunEvents}";
            var b = _bridge;
            BridgeInfoText = b == null ? "—" :
                $"环深 {b.RingDepthMs:F0}ms / 泵入 {b.PumpedSamples / 16000.0:F1}s / 欠载块 {b.UnderrunBlocks}";
        };
        return timer;
    }

    private void EnsureStatsTimer()
    {
        _speakerStatsTimer ??= CreateSpeakerStatsTimer();
        _speakerStatsTimer.Start();
    }

    private void MaybeStopStatsTimer()
    {
        if (!IsSpeakerOn && _bridge == null)
            _speakerStatsTimer?.Stop();
    }

    // ---- 延迟实测（chirp 回环） ----

    /// <summary>
    /// 延迟实测：扬声器播 chirp 训练列 → 笔端麦拾取 → BLE 回传 → 走表剖面 + chirp 扫描 + WAV 留档。
    /// 复用 <see cref="SoftReceiverProbe"/>（CLI srx probe 同一份逻辑，防双拷漂移）；
    /// 开测自动关扩音（防 chirp 混叠/啸叫）；语音流须在流（K2/S1 物理触发）。
    /// </summary>
    [RelayCommand]
    private async Task ProbeAsync()
    {
        if (!CanProbe || Session == null)
            return;
        if (!int.TryParse(ProbeSecondsInput, out var sec))
        {
            Log("[延迟实测] 时长需为数字（10~300 秒）");
            return;
        }
        sec = Math.Clamp(sec, 10, 300);

        IsProbing = true;
        var probe = new SoftReceiverProbe(Session);
        var myCts = new CancellationTokenSource();
        _probeCts = myCts;
        try
        {
            if (IsSpeakerOn)
            {
                ToggleSpeakerExecute();
                Log("[延迟实测] 自动关扩音（防 chirp 混叠/啸叫）");
            }
            if (IsBridgeOn)
            {
                StopBridge("延迟实测前自动关");
                Log("[延迟实测] 自动关虚拟麦克风桥（chirp 勿外泄给第三方）");
            }
            probe.Start(1.0f);
            Log($"[延迟实测] chirp 已开播 → [{probe.RenderDeviceName}]，收流 {sec}s（语音流须在流：K2/S1 物理触发）…");
            ProbeResultText = $"实测中…（{sec}s，期间按 K2 语音键或拨 S1 扩音档让语音流在流）";
            await Task.Delay(TimeSpan.FromSeconds(sec), myCts.Token);
        }
        catch (OperationCanceledException)
        {
            Log("[延迟实测] 已提前中止（断开或取消）");
        }
        catch (Exception ex)
        {
            Log($"[延迟实测] 异常：{ex.Message}");
        }
        finally
        {
            SoftReceiverProbeReport? report = null;
            try
            {
                report = probe.StopAndAnalyze(BuildProbeWavPath());
            }
            catch (Exception ex)
            {
                Log($"[延迟实测] 收尾异常：{ex.Message}");
            }
            myCts.Dispose();
            if (ReferenceEquals(_probeCts, myCts))
                _probeCts = null;
            IsProbing = false;
            if (report != null)
                ShowProbeReport(report);
            else
                ProbeResultText = IsConnected
                    ? "（未测出：解码音频帧不足——语音流未在流，K2/S1 物理触发后再试）"
                    : "（未测：已断开中止）";
        }
    }

    private void ShowProbeReport(SoftReceiverProbeReport r)
    {
        var ppm = r.Scan.Ppm?.ToString("F0") ?? "—";
        ProbeResultText =
            $"走表：步长 {r.Pacing.FittedStepMs:F3}ms（PPM {r.Pacing.Ppm:F0}）｜" +
            $"偏差 p50 {r.Pacing.P50DevMs:F1} / p95 {r.Pacing.P95DevMs:F1} / max {r.Pacing.MaxDevMs:F1} ms（{r.Pacing.Frames} 帧）\n" +
            $"chirp：检出 {r.Scan.DetectedCount}/{r.Scan.ExpectedCount}" +
            (r.Scan.MissingSlots.Count > 0 ? $"（缺失 {r.Scan.MissingSlots.Count} 槽）" : "（无缺失）") +
            $"，PPM {ppm}\n" +
            $"WAV：{r.WavPath}（{r.TotalPcmBytes / 32000.0:F1}s @16k mono）\n" +
            "口径：绝对延迟含起点常量偏置（笔端自由跑钟无共享锚），同场形状/PPM 有效、跨场勿比绝对值";
        Log($"[延迟实测] 完成：走表 PPM {r.Pacing.Ppm:F0} / chirp {r.Scan.DetectedCount}/{r.Scan.ExpectedCount}；WAV={r.WavPath}");
    }

    private static string BuildProbeWavPath() =>
        Path.Combine(AppContext.BaseDirectory, "srx_probe", $"srx_probe_{DateTime.Now:yyyyMMdd_HHmmss}.wav");

    // ---- 虚拟麦克风桥（R1PBleAudio 桥式腿） ----

    /// <summary>
    /// 开/关桥：解码 PCM → 虚拟声卡渲染端，第三方软件从同卡采集端当麦克风录音。
    /// 复用 <see cref="SrxBridgePump"/>（CLI srx bridge 同一份逻辑）；设备枚举每次开桥时刷新。
    /// </summary>
    [RelayCommand]
    private void ToggleBridge()
    {
        if (IsBridgeOn)
        {
            StopBridge("手动关桥");
            return;
        }
        if (!IsConnected || Session == null)
        {
            Log("先连接键盘再开桥");
            return;
        }
        using var enumerator = new MMDeviceEnumerator();
        var renders = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        var captures = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
        var (ri, ci) = SrxBridgePump.SelectPair(
            renders.Select(d => d.FriendlyName).ToList(), captures.Select(d => d.FriendlyName).ToList());
        if (ri < 0)
        {
            Log("未发现虚拟声卡配对端点（CABLE Input/Output 类）——安装 VB-Cable 后重试");
            return;
        }
        try
        {
            var pump = new SrxBridgePump();
            pump.Start(renders[ri], 1.0f);
            _bridge = pump;
            IsBridgeOn = true;
            BridgeText = "开";
            BridgeDeviceText = captures[ci].FriendlyName;
            EnsureStatsTimer();
            Log($"桥已开：解码 PCM → [{renders[ri].FriendlyName}]；第三方软件选麦克风「{captures[ci].FriendlyName}」即 R1 人声（增量≈15~50ms）");
        }
        catch (Exception ex)
        {
            Log($"桥启动失败：{ex.Message}");
        }
    }

    private void StopBridge(string reason)
    {
        var pump = _bridge;
        _bridge = null;
        if (pump != null)
        {
            var pumped = pump.PumpedSamples / 16000.0;
            pump.Dispose();
            Log($"桥已关（{reason}）：本场泵入 {pumped:F1}s 音频");
        }
        IsBridgeOn = false;
        BridgeText = "关";
        BridgeDeviceText = "—";
        BridgeInfoText = "—";
        MaybeStopStatsTimer();
    }

    // ---- 收流录制 WAV（receive 腿） ----

    /// <summary>录制/停止：解码 PCM 直落 WAV（16k/16bit/mono），存 exe 目录 srx_record\。</summary>
    [RelayCommand]
    private void ToggleRecord()
    {
        if (IsRecording)
        {
            StopRecord();
            return;
        }
        if (!IsConnected || Session == null)
        {
            Log("先连接键盘再录制");
            return;
        }
        var dir = Path.Combine(AppContext.BaseDirectory, "srx_record");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"srx_record_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
        try
        {
            _recordWriter = new WaveFileWriter(path, new WaveFormat(MsbcDecoder.SampleRate, 16, 1));
            _recordFrames = 0;
            _recordSw = Stopwatch.StartNew();
            IsRecording = true;
            RecordText = "停止录制";
            Log($"开始录制 WAV → {path}（16k/16bit/mono；语音流须在流）");
        }
        catch (Exception ex)
        {
            Log($"录制启动失败：{ex.Message}");
        }
    }

    private void StopRecord()
    {
        WaveFileWriter? writer;
        lock (_recordLock)
        {
            writer = _recordWriter;
            _recordWriter = null;
        }
        _recordSw?.Stop();
        IsRecording = false;
        RecordText = "录制 WAV";
        if (writer == null)
            return;
        var seconds = _recordFrames * 7.5 / 1000.0;
        try { writer.Dispose(); } catch { /* 收尾容错 */ }
        Log($"录制完成：{_recordFrames} 帧 ≈ {seconds:F1}s（{Path.GetFileName(writer.Filename)}）");
    }

    // ---- 会话事件 → UI（WinRT 线程池回调，一律调度回 UI 线程） ----

    private void OnAudioFrameReady(object? sender, SoftReceiverAudioFrame frame)
    {
        if (IsSpeakerOn)
            _ = _passthrough.WriteExternalPcm(frame.Pcm);
        _bridge?.WritePcm(frame.Pcm);
        lock (_recordLock)
        {
            if (_recordWriter != null)
            {
                _recordWriter.Write(frame.Pcm);
                _recordFrames++;
            }
        }
    }

    private void OnKeyEvent(object? sender, SoftReceiverKeyEvent k) =>
        AppendLog($"键 {(k.IsDown ? "DOWN" : "UP")} page=0x{k.Page:X2} code=0x{k.KeyCode:X2} mod=0x{k.Modifier:X2}");

    private void OnSwitchChanged(object? sender, SoftReceiverSwitchEvent s)
    {
        MainThreadHelper.EnsureOnMainThread(() =>
        {
            ModeText = s.Mode switch
            {
                0 => "IDLE",
                1 => "识别",
                2 => "扩音",
                _ => $"0x{s.Mode:X2}"
            };
        });
        AppendLog($"档位切换 → {s.Mode}");
    }

    private void OnSlotChanged(object? sender, SoftReceiverSlotEvent e)
    {
        if (e.Id == PenStateSlotId.Batt)
        {
            MainThreadHelper.EnsureOnMainThread(() => BatteryText = $"{e.Value}%");
        }
        else if (e.Id == PenStateSlotId.DevState)
        {
            MainThreadHelper.EnsureOnMainThread(() => DevStateText = e.Value switch
            {
                (byte)PenDevState.Normal => "正常",
                (byte)PenDevState.Sleep => "休眠",
                _ => $"0x{e.Value:X2}"
            });
        }
        AppendLog($"槽 0x{e.Id:X2} → {e.Value}");
    }

    private void OnResponseReceived(object? sender, SoftReceiverRspEvent r) =>
        AppendLog($"RSP cmd=0x{r.CmdId:X2} status={r.Status}");

    private void OnDataFrameParsed(object? sender, VpDataFrame frame)
    {
        // 音频流时 DATA 帧 ~133帧/s：充电态只在变化时刷，统计行 500ms 节流（别把 UI 线程打满）
        var prev = Interlocked.Exchange(ref _lastChargeState, frame.ChargeState);
        if (prev != frame.ChargeState)
        {
            MainThreadHelper.EnsureOnMainThread(() => ChargeText = PenStatusText.ChargeStateText(frame.ChargeState));
        }
        var now = Environment.TickCount64;
        if (now - _lastStatsUiMs < 500)
            return;
        Interlocked.Exchange(ref _lastStatsUiMs, now);
        var summary = Session?.Stats.Summary();
        MainThreadHelper.EnsureOnMainThread(() => StatsText = summary ?? "—");
    }

    private long _lastStatsUiMs;
    /// <summary>充电态防重（0xFF=首帧必刷哨兵）</summary>
    private long _lastChargeState = 0xFF;

    private void OnSeqJump(object? sender, long gap) =>
        AppendLog($"seq 跳变缺 {gap} 帧（掩盖为静音）");

    private void OnDiagnostics(object? sender, string msg) => Log(msg);

    private void AppendLog(string line) =>
        MainThreadHelper.EnsureOnMainThread(() =>
        {
            EventLog.Add($"{DateTime.Now:HH:mm:ss.fff}  {line}");
            while (EventLog.Count > EventLogCapacity)
                EventLog.RemoveAt(0);
        });

    private void Log(string line) => AppendLog(line);

    /// <summary>MAC 输入规整（容错空格/小写）；空输入返回 null 走扫描。</summary>
    private static string? NormalizeMac(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var text = raw.Trim().Replace(" ", "").Replace("-", ":").ToUpperInvariant();
        return text.Length == 17 && text.Count(c => c == ':') == 5 ? text : raw.Trim();
    }
}
