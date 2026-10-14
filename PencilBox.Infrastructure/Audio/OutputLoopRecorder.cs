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

    /// <summary>停止并落盘为 16kHz 单声道 16-bit WAV。环回混音格式（float/24bit/Extensible）不做手工辨析，
    /// 统一交给 MediaFoundationResampler 重采样降混——手工 float/short 分支在 Extensible 格式上漏判过
    /// （float 尾数被当 int16 读出 ±1 噪声与伪方波），MF 管线对编码类型不敏感。</summary>
    public async Task StopAndSaveAsync(string path, CancellationToken ct = default)
    {
        if (_capture == null) throw new InvalidOperationException("未启动");
        _capture.StopRecording();
        _capture.DataAvailable -= OnData;
        await Task.Delay(120, ct);                       // 等最后一帧回调落账
        _capture.Dispose();
        _capture = null;

        var fmt = _format!;
        var target = new WaveFormat(16000, 16, 1);
        var monoList = new List<short>();
        _buffer.Position = 0;
        using (var src = new RawSourceWaveStream(_buffer, fmt))
        using (var resampler = new MediaFoundationResampler(src, target) { ResamplerQuality = 60 })
        {
            var buf = new byte[16000 * 2];               // 目标格式下 0.5s 的字节数
            int read;
            while ((read = resampler.Read(buf, 0, buf.Length)) > 0)
            {
                for (var i = 0; i + 1 < read; i += 2)
                    monoList.Add(BitConverter.ToInt16(buf, i));
            }
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        WavFile.WriteMono16(path, monoList.ToArray(), target.SampleRate);
        _logger.LogInformation("环回录制已存: {Path} ({Seconds:F1}s @ {Rate}Hz)", path, monoList.Count / (double)target.SampleRate, target.SampleRate);
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
