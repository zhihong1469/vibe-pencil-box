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

namespace PencilBox.Desktop.ViewModels;

/// <summary>
/// 扩音（PA/喊话）页：采集设备 → 扬声器实时直通 + 引擎附加延迟观测。
/// 延迟测试（端到端标定）为规划中功能，窗口布局已预留位置（见 _share/issues/.../box/03 文档）。
/// </summary>
public partial class PassthroughPageViewModel : ObservableObject
{
    private readonly IAudioPassthrough _passthrough;

    [ObservableProperty]
    private AudioPassthroughState _state = AudioPassthroughState.Idle;

    [ObservableProperty]
    private AudioDeviceInfo? _selectedCaptureDevice;

    [ObservableProperty]
    private AudioDeviceInfo? _selectedRenderDevice;

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
    }

    partial void OnAnsEnabledChanged(bool value)
    {
        if (IsRunning) _passthrough.SetAnsEnabled(value);
    }

    partial void OnAfcEnabledChanged(bool value)
    {
        if (IsRunning) _passthrough.SetAfcEnabled(value);
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

    public PassthroughPageViewModel(IAudioPassthrough passthrough)
    {
        _passthrough = passthrough;
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
                EnableAecGate = AecGateEnabled
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
            StatsSummary = $"时长 {s.ElapsedSeconds,6:F1}s | 环形均值 {s.RingDepthAvgMs,6:F1}ms / 峰值 {s.RingDepthMaxMs,6:F1}ms | " +
                           $"欠载 {s.UnderrunEvents} | 丢弃 {s.DroppedBytes}B | {s.CaptureFormat} → {s.RenderFormat} | 缓冲 {s.RenderLatencyMs}ms | " +
                           $"AEC偏移 {s.AecOffsetMs:F1}ms / DSP最大 {s.DspMaxMs:F2}ms{lim}{gate} | Box段延迟 ~{s.EstimatedLatencyMs:F0}ms";
        });
    }
}
