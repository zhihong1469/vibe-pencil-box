// <copyright file="CompanyPenCmdTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Communication;
using PencilBox.Core.Fsm;
using PencilBox.Core.Protocol.Company;

namespace PencilBox.Tests;

/// <summary>
/// `0x54` 统一TX 转发信封：黄金向量（issues-pencil/12 §2.5 示例字节流）+ 载荷上限
/// （pc-issues/03 C1：57B/56B）+ 会话离线测试（假通道 + 假时钟：重发后成功 / 超时收敛 /
/// 双层 STATUS 归因 / C5 OTA 让路）。
/// </summary>
public class CompanyPenCmdTests
{
    private static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b);

    // ================= Codec：黄金向量 =================

    [Fact]
    public void EncodePenCmd_SetK2_IsGoldenVector()
    {
        // 12 号 §2.5 原例：PC → 基站 : F5 AA AA 05 61 01 54 21 01（信封载荷 = [21 01]）
        var codec = new CompanyCodec();
        var frame = codec.EncodePenCmd(VpTxCommand.BuildSetVoiceKeyMode(0x01));

        Assert.Equal("F5AAAA056101542101", Hex(frame));
        Assert.Equal(9, frame.Length);
        Assert.Equal(0x05, frame[3]);                       // LEN = 3 + 载荷 2
        Assert.Equal(CompanyCmd.PenCmd, frame[6]);
        Assert.Equal(0x21, frame[7]);
        Assert.Equal(0x01, frame[8]);
    }

    [Fact]
    public void EncodePenCmd_PayloadBoundary_57Fits_58Rejected()
    {
        var codec = new CompanyCodec();

        // 57B：4(头) + 3(信封头) + 57 = 64 —— 恰好装满 USB 64B 定长报告（C1 数学）
        var frame = codec.EncodePenCmd(new byte[CompanyCodec.EnvelopePayloadMax]);
        Assert.Equal(64, frame.Length);
        Assert.Equal(0x3C, frame[3]);                       // LEN = 3 + 57 = 60

        // 58B：71B 总长 > 64B 报告 → 编码前置拒绝、不产帧（P-1 修订口径）
        Assert.Throws<ArgumentException>(
            () => codec.EncodePenCmd(new byte[CompanyCodec.EnvelopePayloadMax + 1]));
    }

    [Fact]
    public void TryParsePenCmdResponse_Success_WithAndWithoutData()
    {
        var codec = new CompanyCodec();

        // 12 号 §2.5 原例 RSP：E1 01 54 00 21 00 → STATUS=00 + RSP 原文 [21 00]
        Assert.True(codec.TryParsePenCmdResponse(
            new byte[] { 0x00, 0x21, 0x00 }, out var status, out var cmdId, out var penStatus, out var rsp));
        Assert.Equal(CompanyStatus.Ok, status);
        Assert.Equal(VpTxCommand.SetVoiceKeyMode, cmdId);
        Assert.Equal(CompanyStatus.Ok, penStatus);
        Assert.Empty(rsp);

        // 0x42 查询：RSP = [42 00 01] → rspData = [01]（K2 方式1）
        Assert.True(codec.TryParsePenCmdResponse(
            new byte[] { 0x00, 0x42, 0x00, 0x01 }, out _, out var qid, out _, out var qrsp));
        Assert.Equal(VpTxCommand.QueryVoiceKeyMode, qid);
        Assert.Equal(new byte[] { 0x01 }, qrsp);
    }

    [Fact]
    public void TryParsePenCmdResponse_TerminalStatus_NoRspPayload()
    {
        var codec = new CompanyCodec();

        // 终结层失败（笔未连接 02）：LEN=0x04，仅 [STATUS]，结构合法
        Assert.True(codec.TryParsePenCmdResponse(
            new byte[] { PenCmdStatus.PenNotConnected }, out var status, out var cmdId, out var penStatus, out var rsp));
        Assert.Equal(PenCmdStatus.PenNotConnected, status);
        Assert.Equal(0, cmdId);
        Assert.Equal(0, penStatus);
        Assert.Empty(rsp);
    }

    [Fact]
    public void TryParsePenCmdResponse_InvalidStructures()
    {
        var codec = new CompanyCodec();

        Assert.False(codec.TryParsePenCmdResponse(Array.Empty<byte>(), out _, out _, out _, out _));
        Assert.False(codec.TryParsePenCmdResponse(new byte[] { 0x00, 0x21 }, out _, out _, out _, out _)); // 成功却缺 pen_status
    }

    // ================= 会话：假通道 + 假时钟 =================

    /// <summary>假笔端（串口直连，笔端本地终结）：收到信封请求后回 `E1 01 54 STATUS cmd status`；
    /// <see cref="SilentFirst"/> 控制前 N 次静默（超时重发路径）。</summary>
    private sealed class ScriptedPen : ICommandChannel
    {
        public int SilentFirst { get; set; }
        public byte EnvelopeStatus { get; set; } = PenCmdStatus.Ok;
        public byte PenStatus { get; set; } = CompanyStatus.Ok;
        public List<byte[]> Sent { get; } = new();

        private EventHandler<byte[]>? _onFrame;
        public event EventHandler<byte[]>? FrameReceived { add => _onFrame += value; remove => _onFrame -= value; }
        public ChannelKind Kind => ChannelKind.Serial;
        public bool IsOpen => true;
        public string Describe => "ScriptedPen";

        public Task SendAsync(ReadOnlySpan<byte> frame, CancellationToken ct = default)
        {
            Sent.Add(frame.ToArray());
            if (Sent.Count > SilentFirst)
                _onFrame?.Invoke(this, CompanyFrame.Build(CompanyFrame.BaseCmdResponse, CompanyCmd.PenCmd,
                    EnvelopeStatus == PenCmdStatus.Ok
                        ? new byte[] { EnvelopeStatus, frame[7], PenStatus }
                        : new byte[] { EnvelopeStatus }));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>延时立即完成的假时钟（超时路径秒回；记录全部延时供断言）</summary>
    private sealed class StepClock : IClock
    {
        public List<int> Delays { get; } = new();
        public DateTime UtcNow => new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public Task DelayAsync(int ms, CancellationToken ct = default)
        {
            Delays.Add(ms);
            return Task.CompletedTask;
        }
    }

    private static CompanyPenCmdService CreateService(ICommandChannel channel, StepClock clock, Func<bool>? ota = null) =>
        new(new CompanyCodec(), channel, clock, new CompanyConfig(), ota);

    [Fact]
    public async Task Send_SetK2_SucceedsAndFrameMatchesProposal()
    {
        var pen = new ScriptedPen();
        var clock = new StepClock();
        var service = CreateService(pen, clock);

        var result = await service.SendAsync(VpTxCommand.BuildSetVoiceKeyMode(0x01));

        Assert.True(result.Success);
        Assert.Equal(PenCmdOutcome.Success, result.Outcome);
        Assert.Equal(PenCmdStatus.Ok, result.EnvelopeStatus);
        Assert.Equal(VpTxCommand.SetVoiceKeyMode, result.CmdId);
        Assert.Equal(CompanyStatus.Ok, result.PenStatus);
        Assert.Single(pen.Sent);                                        // 一次即中，不重发
        Assert.Equal("F5AAAA056101542101", Hex(pen.Sent[0]));           // 12 号 §2.5 原例字节流
    }

    [Fact]
    public async Task Send_RetriesAfterSilence_ThenSucceeds()
    {
        var pen = new ScriptedPen { SilentFirst = 1 };
        var clock = new StepClock();
        var service = CreateService(pen, clock);

        var result = await service.SendAsync(VpTxCommand.BuildQueryVoiceKeyMode());

        Assert.True(result.Success);
        Assert.Equal(2, pen.Sent.Count);                                // 首次超时 → 重发 1 次后成功
        Assert.Contains(1000, clock.Delays);                            // 串口形态等待窗 = 1000ms
    }

    [Fact]
    public async Task Send_ExhaustedRetries_ConvergesToTimeout()
    {
        var pen = new ScriptedPen { SilentFirst = int.MaxValue };       // 永不应答（旧固件静默丢弃，C2）
        var clock = new StepClock();
        var service = CreateService(pen, clock);

        var result = await service.SendAsync(VpTxCommand.BuildQueryVoiceKeyMode());

        Assert.False(result.Success);
        Assert.Equal(PenCmdOutcome.Timeout, result.Outcome);
        Assert.Null(result.EnvelopeStatus);                             // 终结层从未应答
        Assert.Equal(4, pen.Sent.Count);                                // 1 + EnvelopeMaxRetry(3)，重发 ≤3
        Assert.Equal(4, clock.Delays.Count(d => d == 1000));            // 每轮一个等待窗，不悬挂
    }

    [Fact]
    public async Task Send_TerminalStatuses_MapToOutcomes()
    {
        // 02 笔未连接 / 01 参数错误：直接归因，不重发语义由上层判断（此处仅验证映射）
        var notConnected = await CreateService(new ScriptedPen { EnvelopeStatus = PenCmdStatus.PenNotConnected },
                new StepClock()).SendAsync(VpTxCommand.BuildQueryVoiceKeyMode());
        Assert.False(notConnected.Success);
        Assert.Equal(PenCmdOutcome.PenNotConnected, notConnected.Outcome);
        Assert.Equal(PenCmdStatus.PenNotConnected, notConnected.EnvelopeStatus);

        var paramError = await CreateService(new ScriptedPen { EnvelopeStatus = PenCmdStatus.ParamError },
                new StepClock()).SendAsync(VpTxCommand.BuildQueryVoiceKeyMode());
        Assert.Equal(PenCmdOutcome.ParamError, paramError.Outcome);
    }

    [Fact]
    public async Task Send_PenRspError_NoResend()
    {
        // 终结层 00 但笔端 status=01：命令已到达笔端，重发无意义 → 单次收敛为 PenRspError
        var pen = new ScriptedPen { PenStatus = CompanyStatus.ParamError };
        var service = CreateService(pen, new StepClock());

        var result = await service.SendAsync(VpTxCommand.BuildSetVoiceKeyMode(0x02));

        Assert.False(result.Success);
        Assert.Equal(PenCmdOutcome.PenRspError, result.Outcome);
        Assert.Single(pen.Sent);
    }

    [Fact]
    public async Task Send_DefersWhileOtaInProgress_ThenProceeds()
    {
        // C5：OTA 会话进行中让路（200ms 轮询），会话结束后补发成功；
        // 闸门自翻（前 3 轮 busy）——确定性，无并发竞态
        var pen = new ScriptedPen();
        var clock = new StepClock();
        int polls = 0;
        var service = CreateService(pen, clock, ota: () => ++polls <= 3);

        var result = await service.SendAsync(VpTxCommand.BuildQueryVoiceKeyMode());

        Assert.True(result.Success);
        Assert.Equal(3, clock.Delays.Count(d => d == 200));             // 让路 3 轮后放行
        Assert.Single(pen.Sent);                                        // 放行后仅发一次
    }

    [Fact]
    public void QueryVoiceKeyMode_PayloadIsSingleCmdId()
    {
        Assert.Equal(new byte[] { 0x42 }, VpTxCommand.BuildQueryVoiceKeyMode());
        Assert.Equal(new byte[] { 0x21, 0x02 }, VpTxCommand.BuildSetVoiceKeyMode(0x02));
    }
}
