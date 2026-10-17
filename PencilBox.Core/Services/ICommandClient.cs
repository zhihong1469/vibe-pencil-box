// <copyright file="ICommandClient.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Protocol;

namespace PencilBox.Core.Services;

/// <summary>
/// PC 命令客户端（事务模型）
/// 发送 CUSTOM 命令并等待对应 Sub-CMD 的应答，支持超时。
/// 由 DeviceService 实现，供 PencilStateService 等业务服务复用。
/// </summary>
public interface ICommandClient
{
    /// <summary>当前是否已连接 Dongle</summary>
    bool IsConnected { get; }

    /// <summary>
    /// 发送 CUSTOM 命令并等待对应 Sub-CMD 应答
    /// </summary>
    /// <param name="subCmd">Sub-CMD</param>
    /// <param name="data">去掉 Sub-CMD 后的载荷（可空）</param>
    /// <param name="timeoutMs">超时毫秒</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>去掉 Sub-CMD 后的应答载荷；超时/失败返回 null</returns>
    Task<byte[]?> SendCustomAsync(
        PcSubCommand subCmd,
        byte[]? data = null,
        int timeoutMs = 5000,
        CancellationToken ct = default);
}
