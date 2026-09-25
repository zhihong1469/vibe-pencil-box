// <copyright file="AudioService.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.IO;
using Microsoft.Extensions.Logging;
using PencilBox.Core.Models;
using PencilBox.Core.Services;

namespace PencilBox.Infrastructure.Services;

/// <summary>
/// 音频服务实现 - 桥接业务接口与底层捕获
/// </summary>
public class AudioService : IAudioService
{
    private readonly ILogger<AudioService> _logger;
    private readonly IAudioCapture _audioCapture;
    private readonly List<AudioFrame> _frames = new();
    private AudioCaptureState _state = AudioCaptureState.Idle;
    private AudioCaptureStats? _latestStats;

    public event EventHandler<AudioFrame>? FrameReceived;
    public event EventHandler<AudioCaptureState>? CaptureStateChanged;
    public event EventHandler<AudioCaptureStats>? StatsUpdated;

    public AudioCaptureState CurrentState => _state;
    public AudioCaptureStats? LatestStats => _latestStats;

    public AudioService(ILogger<AudioService> logger, IAudioCapture audioCapture)
    {
        _logger = logger;
        _audioCapture = audioCapture;

        _audioCapture.AudioDataAvailable += OnAudioDataAvailable;
        _audioCapture.StateChanged += OnCaptureStateChanged;
        _audioCapture.StatsUpdated += OnStatsUpdated;
    }

    /// <summary>
    /// 获取可用音频输入设备（包含 USB Audio 1.0 识别）
    /// </summary>
    public Task<IReadOnlyList<AudioDeviceInfo>> GetAvailableDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        return _audioCapture.GetAvailableDevicesAsync(cancellationToken);
    }

    public async Task StartCaptureAsync(AudioFormat format, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting audio capture: {Format}", format.DisplayFormat);
        await _audioCapture.StartCaptureAsync(null, format.SampleRate, format.Channels, cancellationToken);
    }

    /// <summary>
    /// 从指定设备开始捕获
    /// </summary>
    public async Task StartCaptureAsync(string? deviceId, AudioFormat format,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting audio capture on device '{Device}': {Format}", deviceId, format.DisplayFormat);
        await _audioCapture.StartCaptureAsync(deviceId, format.SampleRate, format.Channels, cancellationToken);
    }

    public async Task StopCaptureAsync()
    {
        _logger.LogInformation("Stopping audio capture...");
        await _audioCapture.StopCaptureAsync();
    }

    public async Task SaveToFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Saving audio to file: {Path}", filePath);

        var data = await _audioCapture.GetPcmDataAsync(cancellationToken);
        if (data.Length == 0)
        {
            _logger.LogWarning("No audio data to save");
            return;
        }

        // 使用实际捕获格式写入标准 WAV 文件（16-bit PCM），确保 DSP 处理正确
        var waveFormat = new NAudio.Wave.WaveFormat(_audioCapture.SampleRate, _audioCapture.BitsPerSample, _audioCapture.Channels);
        await using (var writer = new NAudio.Wave.WaveFileWriter(filePath, waveFormat))
        {
            await writer.WriteAsync(data, 0, data.Length, cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }

        _logger.LogInformation("Audio saved: {Path} ({Bytes} bytes)", filePath, data.Length);
    }

    public IReadOnlyList<AudioFrame> GetCapturedFrames()
    {
        lock (_frames)
        {
            return _frames.AsReadOnly();
        }
    }

    public void ClearCapturedData()
    {
        _audioCapture.ClearPcmData();
        lock (_frames)
        {
            _frames.Clear();
        }
        _logger.LogInformation("Captured data cleared");
    }

    public AudioCaptureStats GetStats()
    {
        return _audioCapture.GetStats();
    }

    private void OnAudioDataAvailable(object? sender, AudioDataAvailableEventArgs e)
    {
        var frame = new AudioFrame
        {
            TimestampUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000,
            Data = e.Data,
            Format = new AudioFormat
            {
                SampleRate = e.SampleRate,
                Channels = e.Channels,
                BitsPerSample = e.BitsPerSample,
                Encoding = AudioEncoding.Pcm
            }
        };

        lock (_frames)
        {
            _frames.Add(frame);
        }

        FrameReceived?.Invoke(this, frame);
    }

    private void OnCaptureStateChanged(object? sender, AudioCaptureStateChangedEventArgs e)
    {
        _state = e.State;
        CaptureStateChanged?.Invoke(this, _state);
    }

    private void OnStatsUpdated(object? sender, AudioCaptureStatsEventArgs e)
    {
        _latestStats = e.Stats;
        StatsUpdated?.Invoke(this, e.Stats);
    }
}
