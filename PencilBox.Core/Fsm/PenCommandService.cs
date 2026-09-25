// <copyright file="PenCommandService.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Communication;
using PencilBox.Core.Protocol.Company;

namespace PencilBox.Core.Fsm;

/// <summary>
/// `0x54` 信封事务的**归因**（pc-issues/03 C3：双层 STATUS 收敛为单值）。
/// `0x00~0x03` 与终结层 STATUS 同值；`PenRspError` = 终结层 00 但笔端 RSP status ≠ 00（业务失败）；
/// `Timeout`/`Unknown` = PC 侧判定（非线上取值）。
/// </summary>
public enum PenCmdOutcome : byte
{
    /// <summary>双层全 00，成功</summary>
    Success = PenCmdStatus.Ok,

    /// <summary>终结层 01：参数错误（含载荷超限）</summary>
    ParamError = PenCmdStatus.ParamError,

    /// <summary>终结层 02：笔未连接（仅无线转发形态）</summary>
    PenNotConnected = PenCmdStatus.PenNotConnected,

    /// <summary>终结层 03：终结方等笔端 RSP 超时</summary>
    PenRespTimeout = PenCmdStatus.PenRespTimeout,

    /// <summary>终结层 00 但笔端 RSP status ≠ 00：业务失败（不再重发——SET 已到达、QUERY 需上层判读）</summary>
    PenRspError = 0x04,

    /// <summary>PC 重发耗尽无应答（含旧固件"未知 CMDTYPE 静默丢弃"场景，pc-issues/03 C2）</summary>
    Timeout = 0xFE,

    /// <summary>应答结构非法 / 未知 STATUS</summary>
    Unknown = PenCmdStatus.Fail
}

/// <summary>`0x54` 信封事务结果（双层字段保留原始值供展示/归因；<see cref="Payload"/> 仅业务成功时有意义）</summary>
public sealed record PenCmdResult(
    bool Success,
    PenCmdOutcome Outcome,
    string Message,
    byte? EnvelopeStatus,
    byte? CmdId,
    byte? PenStatus,
    byte[] Payload)
{
    /// <summary>终结层无应答（重发耗尽）</summary>
    public static PenCmdResult Timeout(int timeoutMs, int attempts) => new(
        false, PenCmdOutcome.Timeout, $"无应答（{timeoutMs}ms × {attempts} 次，不悬挂）",
        null, null, null, Array.Empty<byte>());
}

/// <summary>
/// `0x54` 信封**会话服务**（issues-pencil/12 §2.5；08 号 issue M2）。
///
/// 单帧单事务、无状态机：编码 → （OTA 让路）→ 发送并等 `E1 01 54` → **双层解析** →
/// 超时重发（<see cref="CompanyConfig.EnvelopeMaxRetry"/>）→ 必收敛（红线 5 不悬挂）。
/// 通道按构造传入（串口直连先行；USB→基站转发形态帧型相同，等 R1 后放开）。
/// </summary>
public sealed class CompanyPenCmdService
{
    /// <summary>C5 让路轮询间隔（毫秒）</summary>
    private const int DeferPollMs = 200;

    private readonly ICompanyCodec _codec;
    private readonly ICommandChannel _channel;
    private readonly IClock _clock;
    private readonly CompanyConfig _config;

    /// <summary>
    /// OTA 会话进行中返回 true（C5：**PC 段1 实践 = 升级会话不插发 `0x54`**，本地让路、
    /// 会话终态后补发；CLI 单命令进程不传，UI 宿主接线）。
    /// </summary>
    private readonly Func<bool>? _otaInProgress;

    public CompanyPenCmdService(ICompanyCodec codec, ICommandChannel channel, IClock clock,
        CompanyConfig config, Func<bool>? otaInProgress = null)
    {
        _codec = codec;
        _channel = channel;
        _clock = clock;
        _config = config;
        _otaInProgress = otaInProgress;
    }

    /// <summary>
    /// 信封等待超时（各端宏控）：**串口直连**（笔端本地终结）短窗；**无线形态**必须
    /// > 基站等笔端 RSP 的 1s + 裕量（pc-issues/03 P-3，风格同块级 1500ms"大于下游最坏窗"）。
    /// </summary>
    public int TimeoutMs => _channel.Kind == ChannelKind.Serial
        ? _config.EnvelopeTimeoutSerialMs
        : _config.EnvelopeTimeoutMs;

    /// <summary>发送信封命令并收敛到 <see cref="PenCmdResult"/>（永不悬挂；取消经 <paramref name="ct"/> 抛出）。</summary>
    public async Task<PenCmdResult> SendAsync(byte[] payload, CancellationToken ct = default)
    {
        byte[] request;
        try
        {
            request = _codec.EncodePenCmd(payload);
        }
        catch (ArgumentException ex)
        {
            return new PenCmdResult(false, PenCmdOutcome.ParamError, ex.Message,
                PenCmdStatus.ParamError, null, null, Array.Empty<byte>());
        }

        await DeferWhileOtaInProgressAsync(ct).ConfigureAwait(false);

        var requester = new CommandRequester(_clock, _config);
        var data = await requester.RequestAsync(_channel, request, CompanyCmd.PenCmd,
            ct, TimeoutMs, _config.EnvelopeMaxRetry).ConfigureAwait(false);

        return data == null
            ? PenCmdResult.Timeout(TimeoutMs, _config.EnvelopeMaxRetry + 1)
            : BuildResult(data);
    }

    /// <summary>C5：OTA 会话进行中轮询让路（会话终态后继续；取消即抛出，不悬挂）。</summary>
    private async Task DeferWhileOtaInProgressAsync(CancellationToken ct)
    {
        while (_otaInProgress != null && _otaInProgress())
            await _clock.DelayAsync(DeferPollMs, ct).ConfigureAwait(false);
    }

    /// <summary>双层解析 → 单值归因（pc-issues/03 C3；两层字段原样保留）。</summary>
    private PenCmdResult BuildResult(byte[] data)
    {
        if (!_codec.TryParsePenCmdResponse(data, out var status, out var cmdId, out var penStatus, out var rsp))
            return new PenCmdResult(false, PenCmdOutcome.Unknown, "信封应答结构非法",
                null, null, null, Array.Empty<byte>());

        return status switch
        {
            CompanyStatus.Ok when penStatus == CompanyStatus.Ok => new PenCmdResult(
                true, PenCmdOutcome.Success, "成功", status, cmdId, penStatus, rsp),
            CompanyStatus.Ok => new PenCmdResult(
                false, PenCmdOutcome.PenRspError, $"笔端 RSP 失败：cmd=0x{cmdId:X2} status=0x{penStatus:X2}",
                status, cmdId, penStatus, rsp),
            PenCmdStatus.ParamError => new PenCmdResult(
                false, PenCmdOutcome.ParamError, "终结层 STATUS=01：参数错误（含载荷超限）",
                status, null, null, Array.Empty<byte>()),
            PenCmdStatus.PenNotConnected => new PenCmdResult(
                false, PenCmdOutcome.PenNotConnected, "终结层 STATUS=02：笔未连接",
                status, null, null, Array.Empty<byte>()),
            PenCmdStatus.PenRespTimeout => new PenCmdResult(
                false, PenCmdOutcome.PenRespTimeout, "终结层 STATUS=03：笔端应答超时",
                status, null, null, Array.Empty<byte>()),
            _ => new PenCmdResult(
                false, PenCmdOutcome.Unknown, $"终结层 STATUS=0x{status:X2}：未知",
                status, null, null, Array.Empty<byte>()),
        };
    }
}

/// <summary>
/// 产品级（统一TX §7）命令服务——**形态无关接口**（issues-pencil/12 §2.4"同一套命令语义"）：
/// 基站/串口形态 = F5 `0x54` 信封（<see cref="F5EnvelopeVpCommand"/>）；
/// PC 直连形态（软件接收器，写统一TX CMD 特征）= 后续实现，接口不变。
/// </summary>
public interface IVpCommandService
{
    /// <summary>`0x21` 设置 K2 启动方式（<paramref name="mode"/> = 方式 1/2）</summary>
    Task<PenCmdResult> SetVoiceKeyModeAsync(byte mode, CancellationToken ct = default);

    /// <summary>`0x42` 查询 K2 启动方式（<see cref="PenCmdResult.Payload"/> = `[mode]`）</summary>
    Task<PenCmdResult> QueryVoiceKeyModeAsync(CancellationToken ct = default);

    /// <summary>原始载荷透传（CLI `send --hex` 联调用；语义仍归统一TX 规范 §7）</summary>
    Task<PenCmdResult> SendRawAsync(byte[] payload, CancellationToken ct = default);
}

/// <summary>`0x54` 信封版 <see cref="IVpCommandService"/>（串口先行；USB 形态等基站 R1 后仅换通道）。</summary>
public sealed class F5EnvelopeVpCommand : IVpCommandService
{
    private readonly CompanyPenCmdService _penCmd;

    public F5EnvelopeVpCommand(CompanyPenCmdService penCmd) => _penCmd = penCmd;

    public Task<PenCmdResult> SetVoiceKeyModeAsync(byte mode, CancellationToken ct = default) =>
        _penCmd.SendAsync(VpTxCommand.BuildSetVoiceKeyMode(mode), ct);

    public Task<PenCmdResult> QueryVoiceKeyModeAsync(CancellationToken ct = default) =>
        _penCmd.SendAsync(VpTxCommand.BuildQueryVoiceKeyMode(), ct);

    public Task<PenCmdResult> SendRawAsync(byte[] payload, CancellationToken ct = default) =>
        _penCmd.SendAsync(payload, ct);
}
