// <copyright file="AudioFilePlayer.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PencilBox.Infrastructure.Audio;

/// <summary>
/// 把音频文件经 WASAPI 精确渲染到指定端点（共享模式）。
/// 服务 AEC 评测等自动化测试：测试激励必须确定性地到达目标扬声器，
/// 不走 SoundPlayer/SAPI 的"系统默认"路由（默认端点可能被远程软件虚拟声卡占据）。
/// </summary>
public static class AudioFilePlayer
{
    /// <param name="renderDeviceId">渲染端点 ID；null = 系统默认播放设备</param>
    public static async Task PlayAsync(string filePath, string? renderDeviceId,
        float volume = 1f, CancellationToken ct = default)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = string.IsNullOrWhiteSpace(renderDeviceId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                  .FirstOrDefault(d => d.ID == renderDeviceId)
              ?? throw new ArgumentException($"未找到渲染端点: {renderDeviceId}");

        using var reader = new AudioFileReader(filePath) { Volume = Math.Clamp(volume, 0f, 1f) };
        using var output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, 40);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.Init(new FuncSampleProvider(reader.WaveFormat, reader, done));
        output.Play();
        try { await done.Task.WaitAsync(Timeout.InfiniteTimeSpan, ct); }
        catch (OperationCanceledException) { /* 到时即停（--seconds） */ }
        output.Stop();
    }

    /// <summary>拉到文件尾即完成；Read 返回 0 时触发完成信号</summary>
    private sealed class FuncSampleProvider(WaveFormat format, AudioFileReader reader,
        TaskCompletionSource done) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = format;

        public int Read(float[] buffer, int offset, int count)
        {
            var n = reader.Read(buffer, offset, count);
            if (n == 0)
            {
                done.TrySetResult();
                Array.Clear(buffer, offset, count);
                return count;
            }
            return n;
        }
    }
}
