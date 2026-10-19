// <copyright file="MainViewModel.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PencilBox.Core.Communication;
using PencilBox.Core.Models;
using PencilBox.Core.Services;
using PencilBox.Desktop.Helpers;

namespace PencilBox.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IDeviceService _deviceService;
    private readonly IMacroService _macroService;
    private readonly IAudioService _audioService;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotConnected))]
    private bool _isConnected;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private DeviceInfo? _currentDevice;

    public bool IsNotConnected => !IsConnected;

    public DevicePageViewModel DevicePage { get; }
    public MacroEditorViewModel MacroEditor { get; }
    public AudioRecorderViewModel AudioRecorder { get; }
    public SerialScopeViewModel SerialScope { get; }
    public PassthroughPageViewModel Passthrough { get; }
    public DspPageViewModel DspPage { get; }
    public OtaPageViewModel OtaPage { get; }
    public ProductHomeViewModel ProductHome { get; }
    public AirMouseViewModel AirMouse { get; }

    /// <summary>软触发（13 号：纯软件域，零设备依赖）</summary>
    public SoftTriggerViewModel SoftTrigger { get; }

    /// <summary>功能控制台（14 号：主窗页签显隐与仪表盘控制台卡共用同一单例）</summary>
    public FeaturesViewModel Features { get; }

    public MainViewModel(
        IDeviceService deviceService,
        IMacroService macroService,
        IAudioService audioService,
        DevicePageViewModel devicePage,
        MacroEditorViewModel macroEditor,
        AudioRecorderViewModel audioRecorder,
        SerialScopeViewModel serialScope,
        PassthroughPageViewModel passthrough,
        DspPageViewModel dspPage,
        OtaPageViewModel otaPage,
        ProductHomeViewModel productHome,
        AirMouseViewModel airMouse,
        SoftTriggerViewModel softTrigger,
        FeaturesViewModel features)
    {
        _deviceService = deviceService;
        _macroService = macroService;
        _audioService = audioService;

        DevicePage = devicePage;
        MacroEditor = macroEditor;
        AudioRecorder = audioRecorder;
        SerialScope = serialScope;
        Passthrough = passthrough;
        DspPage = dspPage;
        OtaPage = otaPage;
        ProductHome = productHome;
        AirMouse = airMouse;
        SoftTrigger = softTrigger;
        Features = features;

        _deviceService.ConnectionStateChanged += OnConnectionStateChanged;
        _deviceService.DeviceInfoChanged += OnDeviceInfoChanged;
    }

    private void OnConnectionStateChanged(object? sender, bool connected)
    {
        MainThreadHelper.EnsureOnMainThread(() =>
        {
            IsConnected = connected;
            StatusMessage = connected ? "Device connected" : "Device disconnected";
        });
    }

    private void OnDeviceInfoChanged(object? sender, DeviceInfo? info)
    {
        MainThreadHelper.EnsureOnMainThread(() =>
        {
            CurrentDevice = info;
            if (info != null)
            {
                StatusMessage = $"Connected: {info.Manufacturer} v{info.Version}";
            }
        });
    }
}
