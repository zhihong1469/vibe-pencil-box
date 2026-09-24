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
        ProductHomeViewModel productHome)
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
