// <copyright file="OtaSessionModels.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Fsm;

/// <summary>`0x16` 子命令（`CMDTYPE` 后首字节，骨架 §6.1 锚点帧）</summary>
public static class OtaMode
{
    /// <summary>开始 / 退出 / 应答（`DCMD` 区分：01 开始 / 00 退出 / 02 跳过 / FF 拒绝）</summary>
    public const byte Session = 0x01;

    /// <summary>设备请求 1KB（上行，`PACKL` 固定 0x00）</summary>
    public const byte BlockRequest = 0x0A;

    /// <summary>数据片 48B（下行，22 片/1KB，`PACKL` 0x00~0x15）</summary>
    public const byte DataChunk = 0x07;

    /// <summary>进度 / 终态（上行）</summary>
    public const byte Progress = 0x20;
}

/// <summary>`0x16/01` 的 `DCMD`</summary>
public static class OtaDcmd
{
    /// <summary>开始（PC）/ 允许传输（设备回显）</summary>
    public const byte Start = 0x01;

    /// <summary>退出（PC）/ 退出回显（设备）</summary>
    public const byte Exit = 0x00;

    /// <summary>跳过：已有相同固件（设备）</summary>
    public const byte Skip = 0x02;

    /// <summary>拒绝：参数错/空间不足/不支持（设备）</summary>
    public const byte Reject = 0xFF;
}

/// <summary>`0x20 DCMD=03` 的失败原因（公司 §3.7 值域 + 冻结基线补充）</summary>
public static class OtaFailReason
{
    public static string Describe(byte reason) => reason switch
    {
        0x01 => "参数非法（DOWNTYPE/版本/长度不支持）",
        0x02 => "空间不足",
        0x03 => "版本不匹配",
        0x04 => "CRC 校验失败",
        0x05 => "块级超时（设备多次重查无数据）",
        0x06 => "校验失败（烧写前比对）",
        0x07 => "烧写失败",
        _ => $"未知原因 0x{reason:X2}"
    };
}

/// <summary>OTA 会话状态（拉模式，正文 `上位机/04 §②`）</summary>
public enum OtaFsmState
{
    Idle,

    /// <summary>已发开始帧，等应答</summary>
    StartSent,

    /// <summary>等设备 `0x0A` 拉块 / `0x20` 进度·终态</summary>
    AwaitingRequest,

    /// <summary>正在连发 22×`0x07`</summary>
    Sending1K,

    /// <summary>已发退出帧，等 `DCMD=00` 回显</summary>
    Aborting,

    /// <summary>终态：完成</summary>
    Done,

    /// <summary>终态：失败</summary>
    Failed,

    /// <summary>终态：已退出</summary>
    Aborted
}

/// <summary>
/// 一次升级会话结果。<paramref name="TerminalLost"/> = 成功但终态帧未达
/// （满块 + R2 同版本 skip 确认，issues-pencil/14 方案乙——协议语义上等价终态 `DCMD=02`）。
/// </summary>
public sealed record OtaResult(
    OtaOutcome Outcome,
    int Percent,
    int BlocksDone,
    int TotalBlocks,
    string Message,
    bool TerminalLost = false)
{
    public bool IsTerminalSuccess => Outcome is OtaOutcome.Succeeded or OtaOutcome.Skipped;
}

public enum OtaOutcome
{
    None,
    Succeeded,
    Skipped,
    Failed,
    Aborted,

    /// <summary>
    /// 满块后终态帧未达且设备重新受理会话（ALLOW/0x0A）——已按「不自动重传」停止
    /// （指挥官 10-22 裁决：失败也由人工重试，PC 不自己再传一遍）。结果未知：
    /// 引擎接手主动探测读版本对拍，复核后由人工决定是否重刷。
    /// </summary>
    Unconfirmed
}

/// <summary>进度事件（自算 + 设备 `0x20 DCMD=01` 双轨；自算不替代终态 —— 红线 4）</summary>
public sealed record OtaProgress(int Percent, int BlocksDone, int TotalBlocks, int DevicePercent);

/// <summary>状态转移事件（CLI/UI 订阅）</summary>
public sealed record OtaTransition(OtaFsmState From, OtaFsmState To, string Reason);
