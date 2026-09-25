// <copyright file="IConfigStore.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Models;

namespace PencilBox.Core.Configuration;

/// <summary>
/// 配置存储接口
/// </summary>
public interface IConfigStore
{
    /// <summary>
    /// 加载所有宏配置
    /// </summary>
    Task<IReadOnlyList<MacroConfig>> LoadMacrosAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 保存所有宏配置
    /// </summary>
    Task SaveMacrosAsync(IEnumerable<MacroConfig> macros, CancellationToken cancellationToken = default);

    /// <summary>
    /// 加载应用设置
    /// </summary>
    Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 保存应用设置
    /// </summary>
    Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// 加载 Keymap 本地缓存
    /// </summary>
    Task<IReadOnlyList<KeymapEntry>> LoadKeymapAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 保存 Keymap 本地缓存
    /// </summary>
    Task SaveKeymapAsync(IEnumerable<KeymapEntry> entries, CancellationToken cancellationToken = default);
}

/// <summary>
/// 应用设置
/// </summary>
public class AppSettings
{
    /// <summary>
    /// 默认音频格式
    /// </summary>
    public AudioFormat DefaultAudioFormat { get; set; } = new();

    /// <summary>
    /// HID 通信超时（毫秒）
    /// </summary>
    public int HidTimeoutMs { get; set; } = 5000;

    /// <summary>
    /// 自动连接上次设备
    /// </summary>
    public bool AutoConnect { get; set; } = false;

    /// <summary>
    /// 上次连接的设备路径
    /// </summary>
    public string LastDevicePath { get; set; } = string.Empty;

    /// <summary>
    /// 日志级别
    /// </summary>
    public LogLevel LogLevel { get; set; } = LogLevel.Information;

    /// <summary>
    /// 日志文件路径
    /// </summary>
    public string LogFilePath { get; set; } = "logs/pencilbox.log";

    /// <summary>
    /// 配置文件路径
    /// </summary>
    public string ConfigFilePath { get; set; } = "config/settings.json";
}

/// <summary>
/// 日志级别
/// </summary>
public enum LogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical
}
