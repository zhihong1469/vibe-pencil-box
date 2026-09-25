// <copyright file="IDongleCommandClient.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Services;

/// <summary>
/// Dongle 应答（EP4 命令通道，4A 4C 10…应答帧解析后）。
/// </summary>
public record DongleReply
{
    /// <summary>是否在超时窗口内收到应答</summary>
    public bool Received { get; init; }

    /// <summary>应答通用 status：00 成功 / 01 参数 / 03 Flash / 05 不支持 / 7F 未知</summary>
    public byte Status { get; init; }

    /// <summary>status 之后的载荷（A1 为配对状态体；A2~A4 为空）</summary>
    public byte[] Payload { get; init; } = Array.Empty<byte>();

    public string StatusText => Status switch
    {
        0x00 => "OK",
        0x01 => "PARAM(参数无效)",
        0x02 => "NOT_FOUND/表满",
        0x03 => "FLASH(写失败)",
        0x04 => "PEN_NOT_CONNECTED(笔未连接)",
        0x05 => "UNSUPPORT",
        0x06 => "TIMEOUT",
        0x7F => "UNKNOWN",
        _ => $"0x{Status:X2}"
    };

    public static DongleReply Timeout() => new() { Received = false };
    public static DongleReply Ok(byte status, byte[] payload) =>
        new() { Received = true, Status = status, Payload = payload };
}

/// <summary>
/// EP4 命令通道客户端（发帧 + 等应答 + 解析），面向 Dongle 原生 CMD（A1 GET_STATE / A2 PAIR_NAME /
/// A3 CLEAR_TARGET / A4 SET_TARGET 等）。应用层（UI/CLI）经此与 Dongle 做一问一答。
/// 注：旧 0xA0 承载已退役，命令一律走 EP4(0xFF02/无 RID)；传输层 SendReportAsync(0, frame) 已适配。
/// </summary>
public interface IDongleCommandClient
{
    /// <summary>
    /// 发送原生 CMD 帧并等待 Dongle 应答（ch=0x10 且 CMD 回显匹配）。
    /// </summary>
    /// <param name="cmd">Dongle 原生命令字节（0xA1~0xA4 等）</param>
    /// <param name="data">帧 Data（不含 CMD；可为空）</param>
    /// <param name="timeoutMs">应答超时（默认 1500ms）</param>
    Task<DongleReply> SendCommandAsync(byte cmd, byte[]? data = null,
        int timeoutMs = 1500, CancellationToken ct = default);
}
