// <copyright file="LogRotationPolicy.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Globalization;

namespace PencilBox.Core.SerialScope;

/// <summary>
/// 日志按大小轮转的**策略**（纯逻辑；真正的改名/建文件是 IO，落在 Infrastructure 的写入器里）。
///
/// 口径照抄已验证的参考实现 `serial-chat/core/log_rotate.py`：
/// **写入前**先判断"当前大小 + 本次长度"是否会超限，超限则先轮转再写
/// ⇒ 当前文件**始终包含最新行**（否则崩溃后最新一行会丢在 .1 里）。
/// </summary>
public sealed class LogRotationPolicy
{
    /// <summary>默认上限 10MB（05 §3.4）</summary>
    public const long DefaultMaxBytes = 10L * 1024 * 1024;

    /// <summary>默认备份份数 10（当前 1 份 + 备份 9 份，与参考实现 `backup_count=9` 等价）</summary>
    public const int DefaultBackupCount = 10;

    public LogRotationPolicy(long maxBytes = DefaultMaxBytes, int backupCount = DefaultBackupCount)
    {
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "轮转上限必须为正");
        if (backupCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(backupCount), "备份份数必须为正");

        MaxBytes = maxBytes;
        BackupCount = backupCount;
    }

    /// <summary>单个日志文件的大小上限（字节）</summary>
    public long MaxBytes { get; }

    /// <summary>保留的**文件总份数**（含当前文件）</summary>
    public int BackupCount { get; }

    /// <summary>本次写入是否需要先轮转（写入前判断 ⇒ 当前文件**始终包含最新行**）</summary>
    public bool ShouldRotate(long currentBytes, int incomingBytes)
        => currentBytes + incomingBytes > MaxBytes;

    /// <summary>备份文件名：`serial-scope.log` + 3 ⇒ `serial-scope.log.3`</summary>
    public static string BackupName(string path, int index) => $"{path}.{index}";

    /// <summary>
    /// 解析 CLI 的 `--rotate 10M:10`（大小[:份数]）。大小支持 `K`/`M`/`G` 后缀与纯字节数。
    /// 缺省：大小 <see cref="DefaultMaxBytes"/>、份数 <see cref="DefaultBackupCount"/>。
    /// </summary>
    public static bool TryParse(string? text, out LogRotationPolicy policy)
    {
        policy = new LogRotationPolicy();
        if (string.IsNullOrWhiteSpace(text))
            return true;

        var parts = text.Split(':', 2, StringSplitOptions.TrimEntries);
        if (!TryParseSize(parts[0], out long maxBytes))
            return false;

        int backups = DefaultBackupCount;
        if (parts.Length == 2 && !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out backups))
            return false;

        if (maxBytes <= 0 || backups <= 0)
            return false;

        policy = new LogRotationPolicy(maxBytes, backups);
        return true;
    }

    /// <summary>`10M` / `512K` / `10485760` ⇒ 字节数</summary>
    public static bool TryParseSize(string text, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var value = text.Trim();
        long factor = 1;

        if (value.EndsWith('K') || value.EndsWith('k')) { factor = 1024L; value = value[..^1]; }
        else if (value.EndsWith('M') || value.EndsWith('m')) { factor = 1024L * 1024; value = value[..^1]; }
        else if (value.EndsWith('G') || value.EndsWith('g')) { factor = 1024L * 1024 * 1024; value = value[..^1]; }

        if (!long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long number))
            return false;

        bytes = number * factor;
        return bytes > 0;
    }
}
