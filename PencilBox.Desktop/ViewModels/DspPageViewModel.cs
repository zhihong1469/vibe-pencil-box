using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PencilBox.Core.Models;
using PencilBox.Core.Services;
using PencilBox.Desktop.Helpers;

namespace PencilBox.Desktop.ViewModels;

/// <summary>
/// DSP 分析页面 ViewModel（简单版）
/// 核心流程：选择音频文件（PCM/WAV/其他）→ 交给 Python 处理 → 显示结果
/// </summary>
public partial class DspPageViewModel : ObservableObject
{
    private readonly IDspService _dspService;

    [ObservableProperty]
    private string _inputFilePath = string.Empty;

    [ObservableProperty]
    private DspScriptInfo? _selectedScript;

    [ObservableProperty]
    private int _sampleRate = 16000;

    [ObservableProperty]
    private int _maxTaps = 64;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private string _resultText = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private int _progressPercent;

    [ObservableProperty]
    private string _progressText = string.Empty;

    /// <summary>
    /// 可用 DSP 脚本
    /// </summary>
    public ObservableCollection<DspScriptInfo> Scripts { get; } = new();

    /// <summary>
    /// 文件信息（选择后显示）
    /// </summary>
    public string FileInfo =>
        string.IsNullOrEmpty(InputFilePath) ? string.Empty :
        $"{Path.GetFileName(InputFilePath)}  ({new FileInfo(InputFilePath).Length / 1024.0:F1} KB)";

    /// <summary>
    /// 分析结果摘要（供一键分析显示）
    /// </summary>
    public string AnalysisSummary => Summary;

    public DspPageViewModel(IDspService dspService)
    {
        _dspService = dspService;
        _ = LoadScriptsAsync();
    }

    [RelayCommand]
    private void BrowseFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Audio Source File",
            Filter = "Audio files (*.wav;*.pcm;*.raw)|*.wav;*.pcm;*.raw|WAV files (*.wav)|*.wav|PCM files (*.pcm;*.raw)|*.pcm;*.raw|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            InputFilePath = dialog.FileName;
            OnPropertyChanged(nameof(FileInfo));
            StatusMessage = $"Selected: {Path.GetFileName(dialog.FileName)}";
        }
    }

    [RelayCommand]
    private async Task LoadScriptsAsync()
    {
        try
        {
            StatusMessage = "Loading DSP scripts...";
            var scripts = await Task.Run(() => _dspService.GetAvailableScripts());
            MainThreadHelper.EnsureOnMainThread(() =>
            {
                Scripts.Clear();
                foreach (var s in scripts)
                {
                    Scripts.Add(s);
                }

                // 默认选中一键分析
                SelectedScript = Scripts.FirstOrDefault(s => s.Name == "full_analysis") ?? Scripts.FirstOrDefault();
                StatusMessage = $"Loaded {Scripts.Count} DSP script(s)";
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load scripts: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RunScriptAsync()
    {
        if (string.IsNullOrEmpty(InputFilePath))
        {
            StatusMessage = "Please select an audio file first";
            return;
        }

        if (SelectedScript == null)
        {
            StatusMessage = "Please select a DSP script";
            return;
        }

        await ExecuteAsync(async () =>
        {
            var parameters = new Dictionary<string, object>
            {
                ["sample_rate"] = SampleRate,
                ["max_taps"] = MaxTaps
            };

            var request = new DspRequest(SelectedScript.Name, InputFilePath, null, null, parameters);
            var result = await _dspService.ExecuteAsync(request);

            if (result.Success)
            {
                ResultText = JsonSerializer.Serialize(result.Data, new JsonSerializerOptions { WriteIndented = true });
                StatusMessage = $"Completed in {result.Duration.TotalMilliseconds:F0}ms";
            }
            else
            {
                ResultText = $"Error: {result.Error}";
                StatusMessage = "DSP execution failed";
            }
        });
    }

    [RelayCommand]
    private async Task AnalyzeFullAsync()
    {
        if (string.IsNullOrEmpty(InputFilePath))
        {
            StatusMessage = "Please select an audio file first";
            return;
        }

        await ExecuteAsync(async () =>
        {
            var options = new DspAnalysisOptions
            {
                SampleRate = SampleRate,
                MaxFirTaps = MaxTaps
            };

            var report = await _dspService.AnalyzeFullAsync(InputFilePath, options);

            if (report.FftSpectrum?.Success == true)
            {
                Summary = report.Summary;
            }

            var sections = new Dictionary<string, object?>
            {
                ["waveform"] = report.WaveformStats?.Data,
                ["fft"] = report.FftSpectrum?.Data,
                ["vad"] = report.VadResult?.Data,
                ["filter_design"] = report.FilterDesign?.Data,
                ["agc"] = report.AgcRecommendation?.Data
            };

            ResultText = JsonSerializer.Serialize(sections, new JsonSerializerOptions { WriteIndented = true });
            StatusMessage = "Full analysis completed";
        });
    }

    [RelayCommand]
    private void ClearResult()
    {
        ResultText = string.Empty;
        Summary = string.Empty;
        ProgressPercent = 0;
        ProgressText = string.Empty;
        StatusMessage = "Cleared";
    }

    /// <summary>
    /// 统一的异步执行包装：处理忙状态 + 进度事件
    /// </summary>
    private async Task ExecuteAsync(Func<Task> action)
    {
        if (IsBusy)
        {
            StatusMessage = "Busy, please wait";
            return;
        }

        IsBusy = true;
        ProgressPercent = 0;
        ProgressText = "Starting...";
        StatusMessage = "Working...";

        void OnProgress(object? s, DspProgressEventArgs e)
        {
            MainThreadHelper.EnsureOnMainThread(() =>
            {
                ProgressPercent = e.Percent;
                ProgressText = e.CurrentStep;
            });
        }

        _dspService.ProgressChanged += OnProgress;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ResultText = $"Error: {ex.Message}";
            StatusMessage = "Failed";
        }
        finally
        {
            _dspService.ProgressChanged -= OnProgress;
            ProgressPercent = 100;
            IsBusy = false;
        }
    }
}
