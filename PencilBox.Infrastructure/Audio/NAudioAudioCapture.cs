using System;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Microsoft.Extensions.Logging;
using PencilBox.Core.Models;
using PencilBox.Core.Services;

namespace PencilBox.Infrastructure.Audio;

/// <summary>
/// 基于 NAudio WasapiCapture 的 USB Audio 1.0 捕获实现，带丢包检测
/// </summary>
public class NAudioAudioCapture : IAudioCapture, IDisposable
{
    private readonly ILogger<NAudioAudioCapture> _logger;
    private WasapiCapture? _wasapiCapture;
    private WaveFileWriter? _waveWriter;
    private readonly List<byte> _pcmData = new();
    private readonly object _lock = new();

    private AudioCaptureState _state = AudioCaptureState.Stopped;
    private string? _selectedDeviceId;
    private string? _outputFilePath;
    private bool _disposed;

    // 丢包检测统计
    private readonly AudioCaptureStats _stats = new();
    private DateTime _captureStartTime;
    private DateTime _lastDataTime;
    private long _lastExpectedBytes;

    public AudioCaptureState State => _state;
    public int SampleRate => _wasapiCapture?.WaveFormat.SampleRate ?? 0;
    public int Channels => _wasapiCapture?.WaveFormat.Channels ?? 0;
    public int BitsPerSample => _wasapiCapture?.WaveFormat.BitsPerSample ?? 0;
    public string? SelectedDeviceId => _selectedDeviceId;

    public event EventHandler<AudioDataAvailableEventArgs>? AudioDataAvailable;
    public event EventHandler<AudioCaptureStateChangedEventArgs>? StateChanged;
    public event EventHandler<AudioLevelChangedEventArgs>? AudioLevelChanged;
    public event EventHandler<AudioCaptureStatsEventArgs>? StatsUpdated;

    public NAudioAudioCapture(ILogger<NAudioAudioCapture> logger)
    {
        _logger = logger;
    }

    public Task<IReadOnlyList<AudioDeviceInfo>> GetAvailableDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        var devices = AudioDeviceEnumerator.GetCaptureDevices();
        return Task.FromResult<IReadOnlyList<AudioDeviceInfo>>(devices);
    }

    public async Task StartCaptureAsync(string? deviceId = null, int sampleRate = 48000,
        int channels = 1, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_state == AudioCaptureState.Capturing || _state == AudioCaptureState.Recording)
            return;

        _selectedDeviceId = deviceId;

        try
        {
            StopAndDisposeCapture();
            ResetStats();

            MMDevice? captureDevice = null;
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                var enumerator = new MMDeviceEnumerator();
                captureDevice = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                    .FirstOrDefault(d => d.ID == deviceId);
            }

            captureDevice ??= new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);

            _wasapiCapture = new WasapiCapture(captureDevice);

            // 尝试按请求格式设置；Wasapi 共享模式通常只能用设备当前格式，
            // 所以失败时回退到设备默认格式。
            try
            {
                var requestedFormat = new WaveFormat(sampleRate, 16, channels);
                _wasapiCapture.WaveFormat = requestedFormat;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Device does not support {SampleRate}Hz/{Channels}ch, falling back to {ActualFormat}",
                    sampleRate, channels, _wasapiCapture.WaveFormat);
            }

            _wasapiCapture.DataAvailable += OnDataAvailable;
            _wasapiCapture.RecordingStopped += OnRecordingStopped;

            _captureStartTime = DateTime.Now;
            _lastDataTime = DateTime.Now;
            _lastExpectedBytes = 0;

            _state = AudioCaptureState.Capturing;
            RaiseStateChanged();

            _wasapiCapture.StartRecording();

            _logger.LogInformation("Audio capture started on device '{Device}' ({SampleRate}Hz, {Channels}ch)",
                captureDevice.FriendlyName, sampleRate, channels);

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start audio capture");
            _state = AudioCaptureState.Stopped;
            RaiseStateChanged();
            throw;
        }
    }

    public async Task StopCaptureAsync(CancellationToken cancellationToken = default)
    {
        if (_state != AudioCaptureState.Capturing && _state != AudioCaptureState.Recording)
            return;

        try
        {
            _wasapiCapture?.StopRecording();

            if (_waveWriter != null)
            {
                _waveWriter.Dispose();
                _waveWriter = null;
            }

            _state = AudioCaptureState.Stopped;
            RaiseStateChanged();

            _logger.LogInformation("Audio capture stopped. Captured {Bytes} bytes", _stats.TotalBytesReceived);
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop audio capture");
            throw;
        }
    }

    public async Task StartRecordingAsync(string filePath, string? deviceId = null, int sampleRate = 48000,
        int channels = 1, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_state == AudioCaptureState.Recording)
            return;

        _outputFilePath = filePath;

        try
        {
            await StartCaptureAsync(deviceId, sampleRate, channels, cancellationToken);

            var format = new WaveFormat(sampleRate, 16, channels);
            _waveWriter = new WaveFileWriter(filePath, format);

            _state = AudioCaptureState.Recording;
            RaiseStateChanged();

            _logger.LogInformation("Recording started to: {FilePath}", filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start recording");
            throw;
        }
    }

    public async Task StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        if (_state != AudioCaptureState.Recording && _state != AudioCaptureState.Capturing)
            return;

        await StopCaptureAsync(cancellationToken);

        if (_outputFilePath != null)
        {
            _logger.LogInformation("Recording saved to: {FilePath}", _outputFilePath);
            _outputFilePath = null;
        }
    }

    public Task<byte[]> GetPcmDataAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_pcmData.ToArray());
        }
    }

    public AudioCaptureStats GetStats()
    {
        lock (_lock)
        {
            return new AudioCaptureStats
            {
                TotalBytesReceived = _stats.TotalBytesReceived,
                TotalFramesReceived = _stats.TotalFramesReceived,
                ExpectedBytes = _stats.ExpectedBytes,
                LostBytes = _stats.LostBytes,
                LostRatio = _stats.LostRatio,
                GlitchCount = _stats.GlitchCount,
                CurrentLevel = _stats.CurrentLevel,
                SampleRate = SampleRate,
                Channels = Channels,
                BitsPerSample = BitsPerSample,
                DurationSeconds = _stats.DurationSeconds,
                LastDataTime = _stats.LastDataTime
            };
        }
    }

    public void ClearPcmData()
    {
        lock (_lock)
        {
            _pcmData.Clear();
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
            return;

        byte[] buffer = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, buffer, 0, e.BytesRecorded);

        lock (_lock)
        {
            _pcmData.AddRange(buffer);

            if (_waveWriter != null)
            {
                _waveWriter.Write(buffer, 0, buffer.Length);
            }
        }

        var now = DateTime.Now;
        var elapsed = now - _captureStartTime;
        var sinceLastData = now - _lastDataTime;
        _lastDataTime = now;

        // 丢包检测：
        // 1. 回调间隔异常（超过正常 3 倍）
        // 2. 实际字节数低于按时间推算的预期值
        var format = _wasapiCapture?.WaveFormat;
        int bytesPerSecond = format?.AverageBytesPerSecond ?? 0;
        long expectedBytes = (long)(bytesPerSecond * elapsed.TotalSeconds);

        // 检测 glitch：间隔超过 100ms 或比预期大很多
        double expectedIntervalMs = format != null && format.SampleRate > 0
            ? 1000.0 * _pcmData.Count / bytesPerSecond // rough
            : 10.0;
        if (sinceLastData.TotalMilliseconds > Math.Max(expectedIntervalMs * 3, 100))
        {
            _stats.GlitchCount++;
            _logger.LogWarning("Audio capture glitch detected: interval {IntervalMs:F1}ms", sinceLastData.TotalMilliseconds);
        }

        _stats.TotalBytesReceived += buffer.Length;
        _stats.TotalFramesReceived++;
        _stats.ExpectedBytes = expectedBytes;
        _stats.LostBytes = Math.Max(0, expectedBytes - _stats.TotalBytesReceived);
        _stats.LostRatio = expectedBytes > 0
            ? (double)_stats.LostBytes / expectedBytes
            : 0.0;
        _stats.DurationSeconds = elapsed.TotalSeconds;
        _stats.LastDataTime = now;
        _stats.SampleRate = SampleRate;
        _stats.Channels = Channels;
        _stats.BitsPerSample = BitsPerSample;
        _lastExpectedBytes = expectedBytes;

        AudioDataAvailable?.Invoke(this, new AudioDataAvailableEventArgs(buffer)
        {
            SampleRate = SampleRate,
            Channels = Channels,
            BitsPerSample = BitsPerSample
        });

        float level = CalculateLevel(buffer);
        _stats.CurrentLevel = level;
        AudioLevelChanged?.Invoke(this, new AudioLevelChangedEventArgs(level));

        StatsUpdated?.Invoke(this, new AudioCaptureStatsEventArgs(GetStats()));
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _logger.LogDebug("Recording stopped. Exception: {Exception}", e.Exception?.Message);
    }

    private void RaiseStateChanged()
    {
        StateChanged?.Invoke(this, new AudioCaptureStateChangedEventArgs(_state));
    }

    private void ResetStats()
    {
        lock (_lock)
        {
            _stats.TotalBytesReceived = 0;
            _stats.TotalFramesReceived = 0;
            _stats.ExpectedBytes = 0;
            _stats.LostBytes = 0;
            _stats.LostRatio = 0;
            _stats.GlitchCount = 0;
            _stats.CurrentLevel = 0;
            _stats.DurationSeconds = 0;
        }
    }

    private void StopAndDisposeCapture()
    {
        if (_wasapiCapture != null)
        {
            try
            {
                _wasapiCapture.StopRecording();
                _wasapiCapture.DataAvailable -= OnDataAvailable;
                _wasapiCapture.RecordingStopped -= OnRecordingStopped;
                _wasapiCapture.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing WasapiCapture");
            }
            _wasapiCapture = null;
        }
    }

    private static float CalculateLevel(byte[] buffer)
    {
        if (buffer.Length < 2) return 0;

        int max = 0;
        for (int i = 0; i < buffer.Length - 1; i += 2)
        {
            short sample = (short)((buffer[i + 1] << 8) | buffer[i]);
            int absVal = Math.Abs(sample);
            if (absVal > max) max = absVal;
        }

        return (float)max / short.MaxValue;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopAndDisposeCapture();

        if (_waveWriter != null)
        {
            _waveWriter.Dispose();
            _waveWriter = null;
        }

        lock (_lock)
        {
            _pcmData.Clear();
        }
    }
}
