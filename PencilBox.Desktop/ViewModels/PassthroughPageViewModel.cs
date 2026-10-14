// <copyright file="PassthroughPageViewModel.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PencilBox.Core.Models;
using PencilBox.Core.Services;
using PencilBox.Desktop.Helpers;
using PencilBox.Infrastructure.Audio;

namespace PencilBox.Desktop.ViewModels;

/// <summary>
/// 扩音（PA/喊话）页：采集设备 → 扬声器实时直通 + 引擎附加延迟观测 + 延迟快测（box/07 §2.1）。
/// </summary>
public partial class PassthroughPageViewModel : ObservableObject
{
    private readonly IAudioPassthrough _passthrough;
    private readonly LatencyProbeEngine _probe;

    [ObservableProperty]
    private AudioPassthroughState _state = AudioPassthroughState.Idle;

    [ObservableProperty]
    private AudioDeviceInfo? _selectedCaptureDevice;

    [ObservableProperty]
    private AudioDeviceInfo? _selectedRenderDevice;

    /// <summary>桥接副汇设备（虚拟声卡）：把后级降噪信号复投给其他软件当已降噪麦克风；空 = 关。启动时生效。</summary>
    [ObservableProperty]
    private AudioDeviceInfo? _selectedBridgeDevice;

    /// <summary>音量滑条位置 0~100（对数刻度）；实际增益 = <see cref="PassthroughStartOptions.MaxVolume"/>^(位置/100)。</summary>
    [ObservableProperty]
    private double _volumeSlider;

    /// <summary>实际软件增益（1x~24x），由滑条位置映射。</summary>
    public double Volume => Math.Pow(PassthroughStartOptions.MaxVolume, VolumeSlider / 100.0);

    partial void OnVolumeSliderChanged(double value) => OnPropertyChanged(nameof(Volume));

    // 处理链开关（UI 排列 = 信号处理顺序：AEC → ANS → AFC；限制器常开不占开关）

    [ObservableProperty]
    private bool _aecEnabled = true;

    [ObservableProperty]
    private bool _ansEnabled = true;

    [ObservableProperty]
    private bool _afcEnabled;

    partial void OnAecEnabledChanged(bool value)
    {
        if (IsRunning) _passthrough.SetAecEnabled(value);
        OnPropertyChanged(nameof(ChainWarning));
        OnPropertyChanged(nameof(ChainWarningVisibility));
    }

    partial void OnAnsEnabledChanged(bool value)
    {
        if (IsRunning) _passthrough.SetAnsEnabled(value);
        OnPropertyChanged(nameof(ChainWarning));
        OnPropertyChanged(nameof(ChainWarningVisibility));
    }

    /// <summary>处理链风险预警（板载回环台架实测：box/07 附录B）——AEC/ANS 关闭会显著缩小啸叫裕度</summary>
    public string? ChainWarning => !AecEnabled
        ? "实测：AEC 关闭后裸环约 4x 音量即起啸（AEC 开稳定到 16x），高音量场景请保持 AEC 开"
        : !AnsEnabled
            ? "实测：关 ANS 会连带关闭残余回声抑制，啸叫裕度约减半（8x 即可能起啸）；非排查问题建议保持 ANS 开"
            : null;

    public System.Windows.Visibility ChainWarningVisibility =>
        string.IsNullOrEmpty(ChainWarning) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    partial void OnAfcEnabledChanged(bool value)
    {
        if (IsRunning) _passthrough.SetAfcEnabled(value);
    }

    // 带通降噪（切机械抖动/低频传导与高频嘶声；信号顺序在 AEC 之前；滑条低于下限即该边关闭）

    /// <summary>带通下边滑条 0~400Hz（步进 20）；&lt;40Hz 视为关闭。</summary>
    [ObservableProperty]
    private double _hpfSlider = 80;

    /// <summary>带通上边滑条 0~7800Hz（步进 200）；&lt;2000Hz 视为关闭，默认关。</summary>
    [ObservableProperty]
    private double _lpfSlider;

    public int HpfEffective => HpfSlider < 40 ? 0 : (int)HpfSlider;
    public int LpfEffective => LpfSlider < 2000 ? 0 : (int)LpfSlider;

    public string HpfDisplay => HpfEffective == 0 ? "关" : $"{HpfEffective}Hz";
    public string LpfDisplay => LpfEffective == 0 ? "关" : $"{LpfEffective}Hz";

    partial void OnHpfSliderChanged(double value)
    {
        OnPropertyChanged(nameof(HpfEffective));
        OnPropertyChanged(nameof(HpfDisplay));
        if (IsRunning) _passthrough.SetBandpass(HpfEffective, LpfEffective);
    }

    partial void OnLpfSliderChanged(double value)
    {
        OnPropertyChanged(nameof(LpfEffective));
        OnPropertyChanged(nameof(LpfDisplay));
        if (IsRunning) _passthrough.SetBandpass(HpfEffective, LpfEffective);
    }

    // 调参（自动档=内置推荐；手动档开放滑条，方便边调边听边学）

    [ObservableProperty]
    private bool _aecManual;

    [ObservableProperty]
    private double _aecSuppressDb = 40;

    [ObservableProperty]
    private double _aecSuppressActiveDb = 15;

    [ObservableProperty]
    private bool _ansManual;

    [ObservableProperty]
    private double _ansNoiseSuppressDb = 15;

    /// <summary>AEC 条件触发（实验，默认关）：参考静音满 500ms 旁路 AEC</summary>
    [ObservableProperty]
    private bool _aecGateEnabled;

    partial void OnAecManualChanged(bool value)
    {
        if (IsRunning) _passthrough.SetAecParams(!value, (int)AecSuppressDb, (int)AecSuppressActiveDb);
    }

    partial void OnAecSuppressDbChanged(double value)
    {
        if (IsRunning && AecManual) _passthrough.SetAecParams(false, (int)value, (int)AecSuppressActiveDb);
    }

    partial void OnAecSuppressActiveDbChanged(double value)
    {
        if (IsRunning && AecManual) _passthrough.SetAecParams(false, (int)AecSuppressDb, (int)value);
    }

    partial void OnAnsManualChanged(bool value)
    {
        if (IsRunning) _passthrough.SetAnsParams(!value, (int)AnsNoiseSuppressDb);
    }

    partial void OnAnsNoiseSuppressDbChanged(double value)
    {
        if (IsRunning && AnsManual) _passthrough.SetAnsParams(false, (int)value);
    }

    partial void OnAecGateEnabledChanged(bool value)
    {
        if (IsRunning) _passthrough.SetAecGateEnabled(value);
    }

    [ObservableProperty]
    private int _renderLatencyMs = 40;

    [ObservableProperty]
    private string _statusMessage = "就绪";

    [ObservableProperty]
    private string _statsSummary = "尚未运行";

    [ObservableProperty]
    private double _currentLevel;

    public ObservableCollection<AudioDeviceInfo> CaptureDevices { get; } = new();
    public ObservableCollection<AudioDeviceInfo> RenderDevices { get; } = new();

    public bool IsIdleOrStopped => State is AudioPassthroughState.Idle or AudioPassthroughState.Stopped;
    public bool IsRunning => State is AudioPassthroughState.Running or AudioPassthroughState.Starting;

    public PassthroughPageViewModel(IAudioPassthrough passthrough, LatencyProbeEngine probe)
    {
        _passthrough = passthrough;
        _probe = probe;
        _passthrough.StateChanged += OnStateChanged;
        _passthrough.StatsUpdated += OnStatsUpdated;

        _ = LoadDevicesAsync();
    }

    private async Task LoadDevicesAsync()
    {
        try
        {
            var captures = await _passthrough.GetCaptureDevicesAsync();
            var renders = await _passthrough.GetRenderDevicesAsync();
            MainThreadHelper.EnsureOnMainThread(() =>
            {
                CaptureDevices.Clear();
                foreach (var d in captures) CaptureDevices.Add(d);
                RenderDevices.Clear();
                foreach (var d in renders) RenderDevices.Add(d);

                // 默认：采集端优先选 USB 设备（基站 UAC），渲染端保持 null（系统默认扬声器）
                SelectedCaptureDevice = captures.FirstOrDefault(d => d.IsUsbDevice) ?? captures.FirstOrDefault();
                SelectedRenderDevice = null;
                StatusMessage = $"录音设备 {captures.Count} 个（USB {captures.Count(d => d.IsUsbDevice)}），播放设备 {renders.Count} 个";
            });
        }
        catch (Exception ex)
        {
            MainThreadHelper.EnsureOnMainThread(() => StatusMessage = $"枚举音频设备失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        try
        {
            StatusMessage = "正在启动扩音…";
            await _passthrough.StartAsync(new PassthroughStartOptions
            {
                CaptureDeviceId = SelectedCaptureDevice?.DeviceId,
                RenderDeviceId = SelectedRenderDevice?.DeviceId,
                SampleRate = 16000,
                Channels = 1,
                RenderLatencyMs = RenderLatencyMs,
                Volume = (float)Math.Clamp(Volume, 0.0, PassthroughStartOptions.MaxVolume),
                EnableAec = AecEnabled,
                EnableAns = AnsEnabled,
                EnableAfc = AfcEnabled,
                AecAuto = !AecManual,
                AecSuppressDb = (int)AecSuppressDb,
                AecSuppressActiveDb = (int)AecSuppressActiveDb,
                AnsAuto = !AnsManual,
                AnsNoiseSuppressDb = (int)AnsNoiseSuppressDb,
                EnableAecGate = AecGateEnabled,
                HpfHz = HpfEffective,
                LpfHz = LpfEffective,
                BridgeDeviceId = SelectedBridgeDevice?.DeviceId
            });
            StatusMessage = "扩音中：采集 → 扬声器实时直通（耳机模式下无啸叫风险）";
        }
        catch (Exception ex)
        {
            StatusMessage = $"扩音启动失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        try
        {
            await _passthrough.StopAsync();
            StatusMessage = "已停止";
        }
        catch (Exception ex)
        {
            StatusMessage = $"停止失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RefreshDevicesAsync() => await LoadDevicesAsync();

    // === 延迟快测（box/07 §2.1，CLI audio latency-probe 同源） ===

    [ObservableProperty]
    private bool _probeRunning;

    [ObservableProperty]
    private string _probeResult = "尚未测量";

    /// <summary>脉冲数可调（CLI --pulses 同参数）；间隔/单脉冲时长与 CLI 默认一致。</summary>
    [ObservableProperty]
    private int _probePulses = 12;

    public ObservableCollection<int> ProbePulseChoices { get; } = new(new[] { 4, 8, 12, 20, 30 });

    private const int ProbeIntervalMs = 500;
    private const int ProbePulseMs = 15;

    /// <summary>预计测量时长（音频串 + 采集前后的余量），提示用户保持安静的窗口。</summary>
    public string ProbeEstimatedWindow => $"约 {ProbePulses * ProbeIntervalMs / 1000.0 + 1.5:F0} 秒";

    partial void OnProbePulsesChanged(int value) => OnPropertyChanged(nameof(ProbeEstimatedWindow));

    [RelayCommand]
    private async Task RunLatencyProbeAsync(CancellationToken ct)
    {
        if (ProbeRunning) return;
        if (IsRunning)
        {
            ProbeResult = "先停止扩音再测（麦克风/扬声器被直通占用）";
            return;
        }
        if (SelectedCaptureDevice is null)
        {
            ProbeResult = "先选择采集设备";
            return;
        }

        ProbeRunning = true;
        try
        {
            var pulses = ProbePulses;
            var result = await _probe.RunAsync(
                SelectedCaptureDevice.DeviceId, SelectedRenderDevice?.DeviceId,
                pulses, ProbeIntervalMs, ProbePulseMs,
                amplitude: 0.8f, renderLatencyMs: RenderLatencyMs,
                progress: m => MainThreadHelper.EnsureOnMainThread(() => StatusMessage = $"测延迟：{m}"), ct: ct);
            var stats = LatencyProbeEngine.Analyze(result, pulses, ProbeIntervalMs, ProbePulseMs);
            if (stats.Located == 0)
            {
                ProbeResult = "未检测到脉冲：确认采集设备能听到扬声器（摆放/音量/静音）";
            }
            else
            {
                ProbeResult = $"闭环延迟 p50 {stats.P50:F1}ms | p95 {stats.P95:F1}ms | 抖动 {stats.Jitter:F1}ms | " +
                              $"测量时长 {stats.CapturedSeconds:F1}s" +
                              $"（{stats.Located}/{stats.Expected} 脉冲；数值为物理闭环原值不扣减，测试缓冲影响很小——脉冲包小且断续，渲染缓冲设置只影响扩音）";
                StatusMessage = $"逐脉冲: {string.Join(", ", stats.PerPulseMs.Select(d => d.ToString("F1")))}";
            }
        }
        catch (Exception ex)
        {
            ProbeResult = $"测量失败: {ex.Message}";
        }
        finally
        {
            ProbeRunning = false;
        }
    }

    private void OnStateChanged(object? sender, AudioPassthroughState state)
    {
        MainThreadHelper.EnsureOnMainThread(() =>
        {
            State = state;
            OnPropertyChanged(nameof(IsIdleOrStopped));
            OnPropertyChanged(nameof(IsRunning));
            if (state == AudioPassthroughState.Stopped)
            {
                CurrentLevel = 0;
                StatusMessage = "已停止";
            }
        });
    }

    private void OnStatsUpdated(object? sender, AudioPassthroughStats s)
    {
        MainThreadHelper.EnsureOnMainThread(() =>
        {
            CurrentLevel = s.Level;
            var lim = s.LimiterGain < 0.995f ? $" | 限制器 {s.LimiterGain:F2}（已触顶）" : "";
            var gate = s.AecGateBypassMs > 0 ? $" | 门控旁路 {s.AecGateBypassMs:F0}ms" : "";
            var bridge = s.BridgeDevice.Length == 0 ? "" : $" | 桥接 {s.BridgeDevice}（丢弃 {s.BridgeDroppedBytes}B）";
            StatsSummary = $"时长 {s.ElapsedSeconds,6:F1}s | 环形均值 {s.RingDepthAvgMs,6:F1}ms / 峰值 {s.RingDepthMaxMs,6:F1}ms | " +
                           $"欠载 {s.UnderrunEvents} | 丢弃 {s.DroppedBytes}B | {s.CaptureFormat} → {s.RenderFormat} | 缓冲 {s.RenderLatencyMs}ms | " +
                           $"AEC偏移 {s.AecOffsetMs:F1}ms / DSP最大 {s.DspMaxMs:F2}ms{lim}{gate}{bridge} | Box段延迟 ~{s.EstimatedLatencyMs:F0}ms";
        });
    }
}
