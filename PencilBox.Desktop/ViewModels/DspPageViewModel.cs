// <copyright file="DspPageViewModel.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
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

    [ObservableProperty]
    private BitmapImage? _resultImage;

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
                ResultText = FormatSingleScriptResult(result.Data);
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
                MaxFirTaps = MaxTaps,
                GeneratePlots = true
            };

            var report = await _dspService.AnalyzeFullAsync(InputFilePath, options);

            Summary = report.FftSpectrum?.Success == true ? report.Summary : "Analysis completed";
            ResultText = BuildSummaryText(report);
            ResultImage = LoadFirstPlot(report.Plots);
            StatusMessage = "Full analysis completed";
        });
    }

    [RelayCommand]
    private void ClearResult()
    {
        ResultText = string.Empty;
        Summary = string.Empty;
        ResultImage = null;
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

    // === Helpers ===

    private static BitmapImage? LoadFirstPlot(Dictionary<string, string>? plots)
    {
        if (plots == null || plots.Count == 0)
            return null;

        try
        {
            var bytes = Convert.FromBase64String(plots.First().Value);
            using var ms = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = ms;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static object? GetValue(Dictionary<string, object>? data, params string[] keys)
    {
        if (data == null)
            return null;
        foreach (var key in keys)
        {
            if (data.TryGetValue(key, out var value) && value != null)
                return value;
        }
        return null;
    }

    private static string FormatSingleScriptResult(Dictionary<string, object>? data)
    {
        if (data == null)
            return "No data returned";

        var lines = new List<string>();

        foreach (var kv in data)
        {
            if (kv.Value is string s && s.Length > 120)
            {
                lines.Add($"{kv.Key}: <{s.Length} chars>");
            }
            else if (kv.Value is System.Collections.IEnumerable enumerable && kv.Value is not string)
            {
                var count = 0;
                foreach (var _ in enumerable)
                    count++;
                lines.Add($"{kv.Key}: [{count} items]");
            }
            else
            {
                lines.Add($"{kv.Key}: {kv.Value}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildSummaryText(DspFullReport report)
    {
        var wf = report.WaveformStats?.Data;
        var fft = report.FftSpectrum?.Data;
        var filt = report.FilterDesign?.Data;
        var agc = report.AgcRecommendation?.Data;

        var stats = GetValue(wf, "stats") as Dictionary<string, object>;
        var filtParams = GetValue(filt, "parameters") as Dictionary<string, object>;
        var agcParams = GetValue(agc, "recommended_params") as Dictionary<string, object>;

        var lines = new List<string>
        {
            $"Duration:        {GetValue(wf, "duration_seconds")} s",
            $"Samples:         {GetValue(wf, "num_samples")}",
            $"RMS:             {GetValue(stats, "rms_db")} dBFS",
            $"Peak:            {GetValue(stats, "peak_db")} dBFS",
            $"DC offset:       {GetValue(stats, "dc_offset")}",
            $"Clipping:        {GetValue(stats, "clipping_ratio")}",
            $"Dominant freq:   {GetValue(fft, "dominant_freq")} Hz",
            $"SNR:             {GetValue(fft, "snr_db")} dB",
            $"Filter:          {GetValue(filtParams, "type")} {GetValue(filtParams, "cutoff_hz", "center_hz", "low_hz")} Hz ({GetValue(filtParams, "numtaps")} taps)",
            $"AGC target:      {GetValue(agcParams, "target_level_dbfs")} dBFS",
            $"AGC threshold:   {GetValue(agcParams, "threshold_dbfs")} dBFS"
        };

        return string.Join(Environment.NewLine, lines);
    }
}
