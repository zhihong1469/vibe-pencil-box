// <copyright file="OutputLoopRecorder.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PencilBox.Infrastructure.Audio;

/// <summary>
/// 渲染端环回录制（WASAPI Loopback）：录下扬声器实际播出的内容。
/// 服务 AEC 客观评测——直通输出里的残余回声能量 = AEC 效果的直接度量（先测后改，box/07 纪律）。
/// 一次 Start/Stop 一个实例；与扩音直通并行运行（同一渲染设备允许环回捕获）。
/// </summary>
public sealed class OutputLoopRecorder : IAsyncDisposable
{
    private readonly ILogger _logger;
    private WasapiLoopbackCapture? _capture;
    private readonly MemoryStream _buffer = new();
    private WaveFormat? _format;

    public OutputLoopRecorder(ILogger logger) => _logger = logger;

    /// <param name="renderDeviceId">渲染端点 ID；null = 系统默认播放设备</param>
    public void Start(string? renderDeviceId)
    {
        MMDevice device;
        var enumerator = new MMDeviceEnumerator();
        if (string.IsNullOrWhiteSpace(renderDeviceId))
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        else
            device = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                       .FirstOrDefault(d => d.ID == renderDeviceId)
                   ?? throw new ArgumentException($"未找到渲染端点: {renderDeviceId}");

        _capture = new WasapiLoopbackCapture(device);
        _capture.DataAvailable += OnData;
        _format = _capture.WaveFormat;
        _capture.StartRecording();
        _logger.LogInformation("环回录制启动: {Device} {Format}", device.FriendlyName, _format);
    }

    private void OnData(object? sender, WaveInEventArgs e) => _buffer.Write(e.Buffer, 0, e.BytesRecorded);

    /// <summary>停止并落盘为单声道 16-bit WAV（多声道混叠均分，采样率保持环回格式）</summary>
    public async Task StopAndSaveAsync(string path, CancellationToken ct = default)
    {
        if (_capture == null) throw new InvalidOperationException("未启动");
        _capture.StopRecording();
        _capture.DataAvailable -= OnData;
        await Task.Delay(120, ct);                       // 等最后一帧回调落账
        _capture.Dispose();
        _capture = null;

        var fmt = _format!;
        var bytes = _buffer.GetBuffer();
        var frameBytes = fmt.Channels * (fmt.BitsPerSample / 8);
        var frames = (int)(bytes.Length / frameBytes);
        var mono = new short[frames];
        var isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                      (fmt is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00AA00389B71"));
        var ch = fmt.Channels;
        for (var f = 0; f < frames; f++)
        {
            double acc = 0;
            for (var c = 0; c < ch; c++)
            {
                var idx = (f * ch + c) * (fmt.BitsPerSample / 8);
                acc += isFloat
                    ? BitConverter.ToSingle(bytes, idx)
                    : BitConverter.ToInt16(bytes, idx) / 32768.0;
            }
            var v = acc / ch;
            mono[f] = (short)Math.Clamp(v * 32767, short.MinValue, short.MaxValue);
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        WavFile.WriteMono16(path, mono, fmt.SampleRate);
        _logger.LogInformation("环回录制已存: {Path} ({Seconds:F1}s @ {Rate}Hz)", path, frames / (double)fmt.SampleRate, fmt.SampleRate);
    }

    public async ValueTask DisposeAsync()
    {
        if (_capture != null)
        {
            _capture.DataAvailable -= OnData;
            _capture.Dispose();
            _capture = null;
        }
        await Task.CompletedTask;
    }
}
