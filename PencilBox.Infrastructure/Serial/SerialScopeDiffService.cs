// <copyright file="SerialScopeDiffService.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using Microsoft.Extensions.Logging;
using PencilBox.Core.SerialScope;

namespace PencilBox.Infrastructure.Serial;

/// <summary>
/// 对拍服务（05 §4 S4）：把"最近一次会话"的统一时间轴读出来，交给
/// <see cref="OtaTimelineDiffer"/> 出「缺失 / 重查 / 终态 / 谁先谁后」摘要。
///
/// CLI（`serial diff`）与 UI（串口页"对拍"按钮）**共用本服务** —— 一条口径，两个入口。
/// </summary>
public sealed class SerialScopeDiffService
{
    private readonly ILogger<SerialScopeDiffService> _logger;

    public SerialScopeDiffService(ILogger<SerialScopeDiffService> logger) => _logger = logger;

    /// <summary>主日志路径（给 UI 显示用）</summary>
    public static string ResolveMainPath(string? logDir) => SerialLogFileReader.ResolveMainPath(logDir);

    /// <summary>出对拍摘要</summary>
    /// <param name="logDir">日志目录（null = 默认目录）</param>
    /// <param name="allSessions">true = 看全部历史；false（默认）= 只看最近一次会话</param>
    /// <param name="portFilter">只对拍某个端口标签（null = 全部）</param>
    public OtaTimelineDiff Compare(string? logDir, bool allSessions = false, string? portFilter = null)
    {
        var lines = SerialLogFileReader.ReadSessionLines(logDir, allSessions, portFilter);
        _logger.LogInformation("对拍：读入 {Count} 行（会话={Scope} 端口={Port}）",
            lines.Count, allSessions ? "全部" : "最近一次", portFilter ?? "全部");

        return OtaTimelineDiffer.Compare(lines);
    }
}
