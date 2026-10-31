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
    [ObservableProperty] private bool _isBusy;
    [NotifyPropertyChangedFor(nameof(CanProbe))]
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _connectionText = "未连接";
    [ObservableProperty] private string _macInput = "";
    [ObservableProperty] private string _batteryText = "—";
    [ObservableProperty] private string _chargeText = "—";
    [ObservableProperty] private string _devStateText = "—";
    [ObservableProperty] private string _modeText = "—";
    [ObservableProperty] private string _statsText = "—";
    [ObservableProperty] private bool _isSpeakerOn;
    [ObservableProperty] private string _speakerText = "关";

    // ---- 延迟实测（chirp 回环，复用 SoftReceiverProbe） ----
    [NotifyPropertyChangedFor(nameof(CanProbe))]
    [ObservableProperty] private bool _isProbing;
    [ObservableProperty] private string _probeSecondsInput = "30";
    [ObservableProperty] private string _probeResultText = "（未测）";

    private CancellationTokenSource? _probeCts;

    /// <summary>「开始延迟实测」可用 = 已连接且未在测未忙（按钮 IsEnabled 直绑，勿用转换器）</summary>
    public bool CanProbe => IsConnected && !IsProbing && !IsBusy;

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
            _speakerStatsTimer?.Stop();
            _speakerClock.Reset();
            _ = _passthrough.StopAsync();
            IsSpeakerOn = false;
            SpeakerText = "关";
            Log("扩音泵出已关");
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
            _speakerStatsTimer ??= CreateSpeakerStatsTimer();
            _speakerStatsTimer.Start();
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
        };
        return timer;
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

    // ---- 会话事件 → UI（WinRT 线程池回调，一律调度回 UI 线程） ----

    private void OnAudioFrameReady(object? sender, SoftReceiverAudioFrame frame)
    {
        if (IsSpeakerOn)
            _ = _passthrough.WriteExternalPcm(frame.Pcm);
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
