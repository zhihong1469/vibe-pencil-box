// <copyright file="ICommandChannel.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Communication;

/// <summary>
/// 命令通道种类（公司 V0.9 F5 帧的两条物理链路）
/// </summary>
public enum ChannelKind
{
    /// <summary>USB HID：PC ↔ 基站（0xFF02 无 Report ID，定长 64B 补零）</summary>
    Hid,

    /// <summary>串口：PC ↔ 笔端（工厂直连，115200 变长不补零）</summary>
    Serial
}

/// <summary>
/// 公司 V0.9 命令通道（发送通道层）。
///
/// 职责（03 §2 依赖铁律 3）：**只认"逻辑帧"**——`F5 AA AA | LEN | 负载`（未补零）；
/// 负责串行发一帧、上行一帧；**补零/不补零、定帧由实现自理**；不含命令语义（不认 CMDTYPE、不重试、不聚合状态）。
///
/// 上行帧约定：USB 侧已剥前导 0x00（虚拟报告 ID）并按 LEN 去补零；串口侧已按 LEN 定帧（超时断帧兜底）。
/// 依据：`agent/box/docs/issues/0-待处理/03 §2/§2.1`、`04 §3.2`；骨架 §4.1（双形态）。
/// </summary>
public interface ICommandChannel : IAsyncDisposable
{
    /// <summary>通道种类（Hid / Serial）</summary>
    ChannelKind Kind { get; }

    /// <summary>通道是否可用（USB：已连 0xFF02 无 RID；串口：端口已打开）</summary>
    bool IsOpen { get; }

    /// <summary>人类可读标识（日志/CLI 显示用）</summary>
    string Describe { get; }

    /// <summary>
    /// 发送一帧**逻辑帧**（未补零）。实现负责形态适配：HID 补零到 64B / 串口原样。
    /// </summary>
    Task SendAsync(ReadOnlySpan<byte> logicalFrame, CancellationToken cancellationToken = default);

    /// <summary>上行逻辑帧（已剥 0x00、已按 LEN 定帧/去补零）</summary>
    event EventHandler<byte[]>? FrameReceived;
}
