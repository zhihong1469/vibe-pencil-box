using PencilBox.Core.Ble;
using PencilBox.Core.Communication;
using PencilBox.Core.Protocol.Company;

namespace PencilBox.Core.Fsm;

/// <summary>配对会话状态（显式流转，供 CLI/UI 显示与日志）</summary>
public enum PairFsmState
{
    Idle,

    /// <summary>工厂流程：已发 `0x51` 读（笔端）</summary>
    ReadingPen,

    /// <summary>工厂流程：已发 `0x51` 写（基站）</summary>
    WritingBase,

    /// <summary>工厂流程：已发 `0x51` 回读校验（基站）</summary>
    VerifyingBase,

    /// <summary>用户流程：已发 `0x52`</summary>
    EnteringPairing,

    /// <summary>用户流程：设备回 `01`，等待延时结果 `02/03`</summary>
    WaitingResult,

    /// <summary>成功</summary>
    Success,

    /// <summary>失败（超时/拒绝/校验不一致）</summary>
    Failed
}

/// <summary>一次状态转移（UI/日志订阅）</summary>
public sealed record PairFsmTransition(PairFsmState From, PairFsmState To, string Reason);

/// <summary>用户自主配对（`0x52`）结果</summary>
public sealed record UserPairResult(bool Success, PairInfo? Paired, string Message);

/// <summary>工厂快配对（串口读 → USB 写 → 回读）结果</summary>
public sealed record FactoryPairResult(bool Success, PairInfo? Pen, string Message);

/// <summary>
/// 配对会话状态机（`0x51` 配对信息读写 + `0x52` 进配对）。
///
/// **形态说明**：配对是严格的**请求-应答**型（PC 主动发、设备必答），因此实现为
/// "显式状态枚举 + 异步编排（先挂监听再发 → 等匹配应答 → 超时重发）"，
/// 而不是事件泵；**拉模式**的 `0x16` 升级（设备驱动、需 `OnEvent` 形态）在 S2-3 单独落地（见 `04 §3.3`）。
///
/// **通道不绑定**（`03 §2.1`）：通道按会话传入 —— 工厂流程跨通道串联（串口读笔端、USB 写基站），
/// 用户流程只用 USB。红线：任何等待都有超时并收敛到终态（**不悬挂**）。
/// </summary>
public sealed class CompanyPairFsm
{
    private readonly ICompanyCodec _codec;
    private readonly CompanyConfig _config;
    private readonly CommandRequester _requester;

    public CompanyPairFsm(ICompanyCodec codec, IClock clock, CompanyConfig? config = null)
    {
        _codec = codec;
        _config = config ?? new CompanyConfig();
        _requester = new CommandRequester(clock, _config);
    }

    /// <summary>当前状态</summary>
    public PairFsmState State { get; private set; } = PairFsmState.Idle;

    /// <summary>状态转移事件（CLI/UI 订阅后打印或更新界面）</summary>
    public event EventHandler<PairFsmTransition>? Transitioned;

    /// <summary>
    /// 用户自主配对：向**基站**发 `0x52`，按 `01`（已进入）→ `02/03`（延时结果）流转。
    /// 收 `01` 后**不重复发**（正文 `02 §②B.2`），保持等待态直到 `02/03` 或窗口超时。
    /// </summary>
    public async Task<UserPairResult> EnterPairingAsync(ICommandChannel usbChannel, CancellationToken cancellationToken = default)
    {
        if (!usbChannel.IsOpen)
            return FailUser($"通道未打开：{usbChannel.Describe}（用户配对需基站 USB）");

        MoveTo(PairFsmState.EnteringPairing, "发 0x52 进配对");

        var resp = await _requester.RequestAsync(usbChannel, _codec.EncodePairEnter(), CompanyCmd.PairEnter, cancellationToken)
            .ConfigureAwait(false);

        if (resp == null || !_codec.TryParsePairEnterResp(resp, out var status, out var info))
            return FailUser($"基站未响应 0x52（超时 ×{_config.MaxRetry} 次重发，{_config.ResponseTimeoutMs}ms/次）");

        switch (status)
        {
            case PairEnterStatus.Success:
                MoveTo(PairFsmState.Success, "0x52 直接回 02");
                return new UserPairResult(true, info, $"配对成功：{Describe(info)}");

            case PairEnterStatus.Failed:
                return FailUser("配对失败：设备回 STATUS=03");

            case PairEnterStatus.Entered:
                break;

            default:
                return FailUser($"未预期 STATUS=0x{status:X2}（期望 01/02/03）");
        }

        MoveTo(PairFsmState.WaitingResult, "设备回 01，等待 02/03");

        var result = await _requester.ListenAsync(usbChannel, CompanyCmd.PairEnter, _config.PairingWindowMs, cancellationToken)
            .ConfigureAwait(false);

        if (result == null)
            return FailUser($"等待配对结果超时（{_config.PairingWindowMs}ms 内无 02/03）");

        if (!_codec.TryParsePairEnterResp(result, out var status2, out var info2))
            return FailUser("配对结果帧解析失败");

        if (status2 == PairEnterStatus.Success)
        {
            MoveTo(PairFsmState.Success, "收到 02");
            return new UserPairResult(true, info2, $"配对成功：{Describe(info2)}");
        }

        return status2 == PairEnterStatus.Failed
            ? FailUser("配对失败：设备回 STATUS=03")
            : FailUser($"未预期 STATUS=0x{status2:X2}（等待结果阶段期望 02/03）");
    }

    /// <summary>
    /// 工厂快配对：**串口读笔端 → USB 写基站 → USB 回读校验**（R3 双通道并存；附丁场景）。
    /// 任一步失败即返回失败；回读不一致**不静默通过**。
    /// </summary>
    public async Task<FactoryPairResult> FactoryPairAsync(
        ICommandChannel penChannel, ICommandChannel bsChannel, byte no, CancellationToken cancellationToken = default)
    {
        if (!penChannel.IsOpen || !bsChannel.IsOpen)
        {
            var missing = (!penChannel.IsOpen ? "笔端串口" : "基站 USB");
            return FailFactory($"工厂快配对需**同时**连接笔端串口与基站 USB；当前缺失：{missing}" +
                              $"（笔端：{penChannel.Describe}；基站：{bsChannel.Describe}）");
        }

        // ① 串口读笔端（笔端恒回自身，不看 NO）
        MoveTo(PairFsmState.ReadingPen, $"0x51 读笔端（NO={no}，笔端恒回自身）");
        var readResp = await _requester.RequestAsync(penChannel, _codec.EncodePairRead(no), CompanyCmd.PairInfo, cancellationToken)
            .ConfigureAwait(false);

        if (readResp == null)
            return FailFactory("读取笔端配对信息超时（串口无应答）");

        if (!_codec.TryParsePairReadResp(readResp, out var readStatus, out var pen) || pen == null)
            return FailFactory($"读取笔端配对信息失败：STATUS=0x{readStatus:X2}");

        // ② USB 写基站（MAC 一律显示序，链路序由基站适配层自管 —— R9）
        MoveTo(PairFsmState.WritingBase, $"0x51 写基站（NO={no}，LEN=0x2B）");
        var writeResp = await _requester.RequestAsync(bsChannel, _codec.EncodePairWrite(no, pen.MacDisplay6, pen.BtName),
            CompanyCmd.PairInfo, cancellationToken).ConfigureAwait(false);

        if (writeResp == null || !_codec.TryParsePairWriteResp(writeResp, out var writeStatus, out _))
            return FailFactory("写基站无应答（0x51 写超时）");

        if (writeStatus != CompanyStatus.Ok)
            return FailFactory($"写基站失败：STATUS=0x{writeStatus:X2}（{PairWriteStatusText(writeStatus)}）");

        // ③ USB 回读校验（逐字节一致，不静默通过）
        MoveTo(PairFsmState.VerifyingBase, "0x51 回读校验");
        var verifyResp = await _requester.RequestAsync(bsChannel, _codec.EncodePairRead(no), CompanyCmd.PairInfo, cancellationToken)
            .ConfigureAwait(false);

        if (verifyResp == null)
            return FailFactory("回读基站超时");

        if (!_codec.TryParsePairReadResp(verifyResp, out var verifyStatus, out var readback) || readback == null)
            return FailFactory($"回读基站失败：STATUS=0x{verifyStatus:X2}");

        if (!readback.MacDisplay6.AsSpan().SequenceEqual(pen.MacDisplay6) || readback.BtName != pen.BtName)
        {
            return FailFactory($"配对信息写入不一致（写入 {Describe(pen)} ↔ 回读 {Describe(readback)}）");
        }

        MoveTo(PairFsmState.Success, "回读一致");
        return new FactoryPairResult(true, pen, $"工厂配对成功（NO={no}）：{Describe(pen)}");
    }

    // === 内部：状态与文案 ===

    private void MoveTo(PairFsmState next, string reason)
    {
        var from = State;
        State = next;
        Transitioned?.Invoke(this, new PairFsmTransition(from, next, reason));
    }

    private UserPairResult FailUser(string message)
    {
        MoveTo(PairFsmState.Failed, message);
        return new UserPairResult(false, null, message);
    }

    private FactoryPairResult FailFactory(string message)
    {
        MoveTo(PairFsmState.Failed, message);
        return new FactoryPairResult(false, null, message);
    }

    private static string Describe(PairInfo? info) =>
        info == null
            ? "（无）"
            : $"NO={info.No} MAC={MacAddress.FormatDisplay(info.MacDisplay6)} 名称=\"{info.BtName}\"";

    /// <summary>`0x51` 写应答 STATUS 文案（骨架 §6.2）</summary>
    public static string PairWriteStatusText(byte status) => status switch
    {
        CompanyStatus.Ok => "成功",
        CompanyStatus.ParamError => "参数错（笔端不支持写时恒回此值）",
        CompanyStatus.NoOutOfRange => $"NO 越界（合法 0x00~0x03）",
        CompanyStatus.Fail => "其它失败",
        _ => $"未知 STATUS 0x{status:X2}"
    };
}
