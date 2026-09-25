// <copyright file="IDspService.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Models;

namespace PencilBox.Core.Services;

/// <summary>
/// DSP 信号处理服务接口
/// 当前实现：Python 进程调用（方案三）
/// </summary>
public interface IDspService : IAsyncDisposable
{
    /// <summary>
    /// 验证 Python 环境和依赖是否可用
    /// </summary>
    Task<DspEnvironmentInfo> CheckEnvironmentAsync(CancellationToken ct = default);

    /// <summary>
    /// 获取可用 DSP 脚本列表
    /// </summary>
    IReadOnlyList<DspScriptInfo> GetAvailableScripts();

    /// <summary>
    /// 执行单个 DSP 脚本（批处理模式）
    /// </summary>
    Task<DspResult> ExecuteAsync(DspRequest request, CancellationToken ct = default);

    /// <summary>
    /// 一键全链路分析（R1->R2->R4->R5/R7->R8->R9）
    /// </summary>
    Task<DspFullReport> AnalyzeFullAsync(
        string inputFilePath,
        DspAnalysisOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// 执行状态变化事件（进度、日志）
    /// </summary>
    event EventHandler<DspProgressEventArgs>? ProgressChanged;
}
