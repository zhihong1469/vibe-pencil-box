// <copyright file="IAudioCapture.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Models;

namespace PencilBox.Core.Services;

/// <summary>
/// 音频捕获接口（低层）
/// </summary>
public interface IAudioCapture : IDisposable
{
    /// <summary>
    /// 当前捕获状态
    /// </summary>
    AudioCaptureState State { get; }

    /// <summary>
    /// 当前采样率
    /// </summary>
    int SampleRate { get; }

    /// <summary>
    /// 当前声道数
    /// </summary>
    int Channels { get; }

    /// <summary>
    /// 当前位深度
    /// </summary>
    int BitsPerSample { get; }

    /// <summary>
    /// 当前选中的设备 ID
    /// </summary>
    string? SelectedDeviceId { get; }

    /// <summary>
    /// 音频数据可用事件
    /// </summary>
    event EventHandler<AudioDataAvailableEventArgs>? AudioDataAvailable;

    /// <summary>
    /// 捕获状态变化事件
    /// </summary>
    event EventHandler<AudioCaptureStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// 音频电平变化事件
    /// </summary>
    event EventHandler<AudioLevelChangedEventArgs>? AudioLevelChanged;

    /// <summary>
    /// 捕获统计信息更新事件
    /// </summary>
    event EventHandler<AudioCaptureStatsEventArgs>? StatsUpdated;

    /// <summary>
    /// 获取所有可用音频输入设备
    /// </summary>
    Task<IReadOnlyList<AudioDeviceInfo>> GetAvailableDevicesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 开始从指定设备捕获音频
    /// </summary>
    /// <param name="deviceId">设备 ID，null 则使用系统默认设备</param>
    /// <param name="sampleRate">采样率</param>
    /// <param name="channels">声道数</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task StartCaptureAsync(string? deviceId = null, int sampleRate = 48000, int channels = 1,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 停止音频捕获
    /// </summary>
    Task StopCaptureAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 开始录制到文件
    /// </summary>
    Task StartRecordingAsync(string filePath, string? deviceId = null, int sampleRate = 48000,
        int channels = 1, CancellationToken cancellationToken = default);

    /// <summary>
    /// 停止录制
    /// </summary>
    Task StopRecordingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取已捕获的 PCM 数据
    /// </summary>
    Task<byte[]> GetPcmDataAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取当前捕获统计信息
    /// </summary>
    AudioCaptureStats GetStats();

    /// <summary>
    /// 清空已缓存的 PCM 数据
    /// </summary>
    void ClearPcmData();
}

/// <summary>
/// 音频数据可用事件参数
/// </summary>
public class AudioDataAvailableEventArgs : EventArgs
{
    /// <summary>
    /// PCM 数据
    /// </summary>
    public byte[] Data { get; }

    /// <summary>
    /// 采样率
    /// </summary>
    public int SampleRate { get; init; }

    /// <summary>
    /// 声道数
    /// </summary>
    public int Channels { get; init; }

    /// <summary>
    /// 位深度
    /// </summary>
    public int BitsPerSample { get; init; }

    public AudioDataAvailableEventArgs(byte[] data)
    {
        Data = data;
    }
}

/// <summary>
/// 音频捕获状态变化事件参数
/// </summary>
public class AudioCaptureStateChangedEventArgs : EventArgs
{
    /// <summary>
    /// 当前状态
    /// </summary>
    public AudioCaptureState State { get; }

    public AudioCaptureStateChangedEventArgs(AudioCaptureState state)
    {
        State = state;
    }
}

/// <summary>
/// 音频电平变化事件参数
/// </summary>
public class AudioLevelChangedEventArgs : EventArgs
{
    /// <summary>
    /// 电平值（0.0 ~ 1.0）
    /// </summary>
    public float Level { get; }

    public AudioLevelChangedEventArgs(float level)
    {
        Level = level;
    }
}

/// <summary>
/// 捕获统计信息更新事件参数
/// </summary>
public class AudioCaptureStatsEventArgs : EventArgs
{
    /// <summary>
    /// 统计信息
    /// </summary>
    public AudioCaptureStats Stats { get; }

    public AudioCaptureStatsEventArgs(AudioCaptureStats stats)
    {
        Stats = stats;
    }
}
