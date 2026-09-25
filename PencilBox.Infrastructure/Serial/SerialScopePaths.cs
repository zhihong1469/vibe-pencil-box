// <copyright file="SerialScopePaths.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Infrastructure.Serial;

/// <summary>
/// 串口抓日志的**落点**（单点定义，CLI 的 log/tail/grep 共用一套路径，避免各写一份而漂移）。
///
/// 与既有 <see cref="Config.JsonConfigStore"/> 同规：一律落在 `%LocalAppData%/PencilBox` 下。
/// </summary>
public static class SerialScopePaths
{
    /// <summary>`%LocalAppData%/PencilBox`</summary>
    public static string BaseDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PencilBox");

    /// <summary>默认日志目录：`%LocalAppData%/PencilBox/logs/serial`</summary>
    public static string DefaultLogDir => Path.Combine(BaseDir, "logs", "serial");

    /// <summary>默认配置：`%LocalAppData%/PencilBox/config/serial.json`</summary>
    public static string DefaultConfigPath => Path.Combine(BaseDir, "config", "serial.json");

    /// <summary>解析日志目录（`--log-dir` 覆盖优先；相对路径按当前目录展开）</summary>
    public static string ResolveLogDir(string? overrideDir)
        => string.IsNullOrWhiteSpace(overrideDir) ? DefaultLogDir : Path.GetFullPath(overrideDir);
}
