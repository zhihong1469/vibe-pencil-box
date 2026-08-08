using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PencilBox.Core.Models;
using PencilBox.Core.Services;
using PencilBox.Desktop.Helpers;

namespace PencilBox.Desktop.ViewModels;

public partial class AudioRecorderViewModel : ObservableObject
{
    private readonly IAudioService _audioService;

    [ObservableProperty]
    private AudioCaptureState _captureState = AudioCaptureState.Idle;

    [ObservableProperty]
    private AudioFormat _selectedFormat = new();

    [ObservableProperty]
    private AudioDeviceInfo? _selectedDevice;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private int _frameCount;

    [ObservableProperty]
    private double _durationSeconds;

    [ObservableProperty]
    private string _outputFilePath = "audio_capture.wav";

    [ObservableProperty]
    private string _statsSummary = "No stats yet";

    [ObservableProperty]
    private double _currentLevel;

    public List<AudioFormat> AvailableFormats { get; } = new()
    {
        new AudioFormat { SampleRate = 8000, BitsPerSample = 16, Channels = 1, Encoding = AudioEncoding.Pcm },
        new AudioFormat { SampleRate = 16000, BitsPerSample = 16, Channels = 1, Encoding = AudioEncoding.Pcm },
        new AudioFormat { SampleRate = 44100, BitsPerSample = 16, Channels = 1, Encoding = AudioEncoding.Pcm },
        new AudioFormat { SampleRate = 48000, BitsPerSample = 16, Channels = 1, Encoding = AudioEncoding.Pcm }
    };

    public ObservableCollection<AudioDeviceInfo> AvailableDevices { get; } = new();

    public AudioRecorderViewModel(IAudioService audioService)
    {
        _audioService = audioService;
        _audioService.FrameReceived += OnFrameReceived;
        _audioService.CaptureStateChanged += OnCaptureStateChanged;
        _audioService.StatsUpdated += OnStatsUpdated;

        _ = LoadDevicesAsync();
    }

    private async Task LoadDevicesAsync()
    {
        try
        {
            var devices = await _audioService.GetAvailableDevicesAsync();
            MainThreadHelper.EnsureOnMainThread(() =>
            {
                AvailableDevices.Clear();
                foreach (var device in devices)
                {
                    AvailableDevices.Add(device);
                }

                // 默认选中第一个 USB Audio 1.0 设备，否则选第一个
                SelectedDevice = devices.FirstOrDefault(d => d.IsUsbDevice) ?? devices.FirstOrDefault();
                StatusMessage = $"Found {devices.Count} audio device(s), {devices.Count(d => d.IsUsbDevice)} USB";
            });
        }
        catch (Exception ex)
        {
            MainThreadHelper.EnsureOnMainThread(() =>
            {
                StatusMessage = $"Failed to enumerate devices: {ex.Message}";
            });
        }
    }

    private void OnFrameReceived(object? sender, AudioFrame frame)
    {
        MainThreadHelper.EnsureOnMainThread(() =>
        {
            FrameCount++;
            DurationSeconds = FrameCount * SelectedFormat.SamplesPerFrame / (double)SelectedFormat.SampleRate;
        });
    }

    private void OnCaptureStateChanged(object? sender, AudioCaptureState state)
    {
        MainThreadHelper.EnsureOnMainThread(() =>
        {
            CaptureState = state;
            StatusMessage = state switch
            {
                AudioCaptureState.Capturing => "Capturing audio...",
                AudioCaptureState.Recording => "Recording audio...",
                AudioCaptureState.Paused => "Paused",
                AudioCaptureState.Saving => "Saving file...",
                _ => "Ready"
            };
        });
    }

    private void OnStatsUpdated(object? sender, AudioCaptureStats stats)
    {
        MainThreadHelper.EnsureOnMainThread(() =>
        {
            StatsSummary = stats.Summary;
            CurrentLevel = stats.CurrentLevel;
            DurationSeconds = stats.DurationSeconds;
        });
    }

    [RelayCommand]
    private async Task RefreshDevicesAsync()
    {
        StatusMessage = "Refreshing devices...";
        await LoadDevicesAsync();
    }

    [RelayCommand]
    private async Task StartCaptureAsync()
    {
        try
        {
            FrameCount = 0;
            DurationSeconds = 0;
            StatsSummary = "No stats yet";
            CurrentLevel = 0;
            StatusMessage = "Starting capture...";

            string? deviceId = SelectedDevice?.DeviceId;
            await _audioService.StartCaptureAsync(deviceId, SelectedFormat);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task StopCaptureAsync()
    {
        try
        {
            await _audioService.StopCaptureAsync();
            StatusMessage = $"Captured {FrameCount} frames ({DurationSeconds:F1}s)";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveToFileAsync()
    {
        try
        {
            if (FrameCount == 0)
            {
                StatusMessage = "No audio data to save";
                return;
            }

            var dialog = new SaveFileDialog
            {
                Filter = "WAV audio files (*.wav)|*.wav|All files (*.*)|*.*",
                DefaultExt = "wav",
                FileName = OutputFilePath,
                Title = "Save Captured Audio"
            };

            if (dialog.ShowDialog() != true)
            {
                StatusMessage = "Save cancelled";
                return;
            }

            StatusMessage = "Saving...";
            await _audioService.SaveToFileAsync(dialog.FileName);
            OutputFilePath = Path.GetFileName(dialog.FileName);
            StatusMessage = $"Saved to {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ClearData()
    {
        _audioService.ClearCapturedData();
        FrameCount = 0;
        DurationSeconds = 0;
        CurrentLevel = 0;
        StatsSummary = "No stats yet";
        StatusMessage = "Data cleared";
    }
}
