// <copyright file="IClock.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Fsm;

/// <summary>
/// 时间抽象（**仅在 FSM 边界使用**）：把"超时/节拍"变成可注入项，
/// 单测用假时钟即可离线跑全时序（依据：`03 §11 决策 2`）。
/// </summary>
public interface IClock
{
    /// <summary>当前 UTC 时间（日志/计时用）</summary>
    DateTime UtcNow { get; }

    /// <summary>等待指定毫秒（可取消）</summary>
    Task DelayAsync(int milliseconds, CancellationToken cancellationToken = default);
}

/// <summary>真实时间实现（生产用）</summary>
public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;

    public Task DelayAsync(int milliseconds, CancellationToken cancellationToken = default) =>
        milliseconds <= 0 ? Task.CompletedTask : Task.Delay(milliseconds, cancellationToken);
}
