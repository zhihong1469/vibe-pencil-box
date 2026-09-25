// <copyright file="SerialLogFileReader.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Text;
using PencilBox.Core.SerialScope;

namespace PencilBox.Infrastructure.Serial;

/// <summary>
/// 读本工具写下的日志文件（`tail` / `grep` / `diff` 共用一条读路径，避免三处各写一份）。
///
/// 两个要点：
/// 1. **共享读**：写入侧用的是 `FileShare.ReadWrite`，这里也必须用同一共享模式 ——
///    否则"`serial log` 还在跑的时候 `serial tail`"会直接撞"文件被占用"；
/// 2. **按会话切片**：默认只看**最近一次** `===== SESSION START =====` 之后的行
///    （与参考实现 `serial-chat --tail` 同口径），`--session all` 才看全部。
/// </summary>
public static class SerialLogFileReader
{
    /// <summary>主日志绝对路径</summary>
    public static string ResolveMainPath(string? logDir)
        => Path.Combine(SerialScopePaths.ResolveLogDir(logDir), SerialLogWriter.MainFileName);

    /// <summary>逐行读文本（允许文件正被写入）</summary>
    public static List<string> ReadAllLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return lines;
    }

    /// <summary>切出"最近一次会话"（找不到标记则原样返回）</summary>
    public static List<string> SliceAfterLastSessionStart(List<string> rows)
    {
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            if (rows[i].Contains(SerialLogWriter.SessionMarker, StringComparison.Ordinal))
                return rows.Skip(i + 1).ToList();
        }
        return rows;
    }

    /// <summary>按端口标签过滤（主日志里标签是行首第 2 组方括号）</summary>
    public static List<string> FilterByPort(List<string> rows, string? port)
        => string.IsNullOrWhiteSpace(port)
            ? rows
            : rows.Where(r => r.Contains($"][{port}][", StringComparison.Ordinal)).ToList();

    /// <summary>
    /// 读出一次会话并**反解析成统一时间轴**（对拍 / UI 用）。
    /// 反解析不了的行（比如手工加的注释）直接跳过 —— 对拍只认真实日志行。
    /// </summary>
    public static List<LogLine> ReadSessionLines(string? logDir, bool allSessions, string? portFilter)
    {
        var path = ResolveMainPath(logDir);
        if (!File.Exists(path))
            throw new FileNotFoundException($"日志文件不存在：{path}（先跑 `pencilbox serial log`）", path);

        var rows = ReadAllLinesShared(path);
        if (!allSessions)
            rows = SliceAfterLastSessionStart(rows);
        rows = FilterByPort(rows, portFilter);

        return rows
            .Select(row => LogLineParser.TryParseMainLog(row, out var line) ? line : null)
            .Where(line => line != null)
            .Select(line => line!)
            .ToList();
    }
}
