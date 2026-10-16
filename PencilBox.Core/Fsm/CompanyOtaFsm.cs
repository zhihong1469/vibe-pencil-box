// <copyright file="CompanyOtaFsm.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Threading.Channels;
using PencilBox.Core.Communication;
using PencilBox.Core.Protocol.Company;

namespace PencilBox.Core.Fsm;

/// <summary>
/// 公司 V0.9 `0x16` 拉模式升级会话状态机（纯逻辑，零 IO —— 只经 `ICommandChannel`/`IClock` 接口）。
///
/// 驱动模型：设备拉（收到 `0x0A` 才发 22×`0x07`）、`0x20` 报进度/终态（正文 `上位机/04`）：
/// - `Idle → StartSent → AwaitingRequest ⇄ Sending1K → Done/Failed/Aborted`；
/// - **R1** 块级重查：任意态收 `0x0A` → 当前块作废、按新 `PACKH` 重发（含 `Sending1K` 被打断）；
/// - **R2**（B1 冻结口径，issues/05 §1-B / issues/08 PC-3）：块等待超时（**1500ms，锚点 = 本块首片发出**）
///   → **首选继续等待**；"重发开始帧"降为**最后手段**（一次性，会重启会话）→ 仍无 → `Failed`（不悬挂）；
/// - **R4** 退出后等回显超时 → `Failed`（不悬挂）；
/// - **R5** 越界 `PACKH` → 忽略 + 告警（禁止补零糊弄）；
/// - **终态窗**（issues-pencil/14 方案乙）：满块（自算 100%）后末块改入**等终态窗**
///   （`OtaTerminalWaitMs` ≥5s，覆盖设备 VERIFY+activate+复位）；窗内收 `0x20 DCMD=02/03` → Done/Failed
///   （既有路径，不受状态门控）；窗超时 → **R2 重发开始帧确认**：满块 + 同版本 skip ⇒
///   **成功（终态丢失）**（协议语义等价终态 02——设备只会在已跑新固件时答同版本）；R2 答 ALLOW
///   则视为会话重启续走（终态丢失推断作废）；R2 已用或非 skip → `Failed`（不悬挂）；
/// - **PC-4**（issues/05 §1-G）：同块重查超阈值**仅告警**（保险计数，不作协议失败信号——
///   块级失败的**唯一权威** = 设备终态 `0x20 DCMD=02/03`）；
/// - **PC-6**（issues/05 §2.3-3）：`0x0A` 可能与开始应答 `0x01(DCMD=01)` 几乎同时到达 →
///   `StartSent` 态收 `0x0A` 视为**隐式允许**（设备只有 ALLOW 后才拉块）；
/// - **E23** `0x0A` 的 `DOWNTYPE` ≠ 会话目标 → 忽略 + 告警；我发的 `0x07` 恒回显会话目标。
/// </summary>
public sealed class CompanyOtaFsm
{
    private const int BytesPerBlock = 1024;
    private const int BytesPerChunk = 48;
    private const int ChunksPerBlock = 22;    // 21×48 + 16 = 1024

    private readonly ICompanyCodec _codec;
    private readonly ICommandChannel _channel;
    private readonly IClock _clock;
    private readonly CompanyConfig _config;
    private readonly Channel<OtaEvent> _events = Channel.CreateUnbounded<OtaEvent>(
        new UnboundedChannelOptions { SingleReader = true });

    private byte[] _image = Array.Empty<byte>();
    private Downtype _target;
    private byte _hver;
    private byte _sver1, _sver2, _sver3;
    private ushort _crc;
    private int _totalBlocks;
    private int _blocksDone;
    private int _devicePercent = -1;
    private int _startRetry;
    private int _exitAttempts;
    private bool _aborted;                      // 用户/上位机主动取消
    private OtaOutcome _outcome = OtaOutcome.None;
    private bool _terminalLostCandidate;        // 14号 方案乙：满块后终态窗超时 → R2 确认在途
    private bool _terminalLostConfirmed;        // R2 答 同版本 skip → 判"成功（终态丢失）"

    // ===== B1 冻结口径（issues/08 PC-2/3/4）=====
    private DateTime _blockDeadline = DateTime.MaxValue;   // PC-2：块等待锚点 = 本块首片发出 + BlockTimeoutMs
    private int _blockWaitTimeouts;                        // PC-3：块等待"继续等待"轮数（保险计数）
    private bool _lastResortSent;                          // PC-3：最后手段（重发开始帧）只用一次，防会话重启无限循环
    private byte _lastRequestedPackH = 0xFF;               // PC-4：同块重查计数（仅告警）
    private int _sameBlockRequests;

    public CompanyOtaFsm(ICompanyCodec codec, ICommandChannel channel, IClock clock, CompanyConfig config)
    {
        _codec = codec;
        _channel = channel;
        _clock = clock;
        _config = config;
    }

    public OtaFsmState State { get; private set; } = OtaFsmState.Idle;

    public event EventHandler<OtaTransition>? Transitioned;
    public event EventHandler<OtaProgress>? Progress;
    public event EventHandler<string>? Warning;

    /// <summary>
    /// 执行一次升级会话（发开始 → 事件泵直到终态）。
    /// </summary>
    /// <param name="abortAfterBlock">发完第 N 块（1 起）后主动退出（`DCMD=00`）；`-1` 不退出</param>
    public async Task<OtaResult> RunAsync(byte[] image, Downtype target,
        byte hver, byte sver1, byte sver2, byte sver3,
        int abortAfterBlock = -1, int chunkGapMs = -1, CancellationToken cancellationToken = default)
    {
        if (State != OtaFsmState.Idle)
            throw new InvalidOperationException($"单会话互斥：当前状态 {State}（红线 5）");
        if (image.Length == 0)
            throw new ArgumentException("镜像为空", nameof(image));
        if (image.Length > 256 * BytesPerBlock)
            Warning?.Invoke(this, $"镜像 {image.Length}B 超 256KB（DOWNID>0 本版不支持，仅告警）");

        (_image, _target, _hver, _sver1, _sver2, _sver3) = (image, target, hver, sver1, sver2, sver3);
        _crc = Crc16CcittFalse.Compute(image);
        _totalBlocks = (image.Length + BytesPerBlock - 1) / BytesPerBlock;
        _blockDeadline = DateTime.MaxValue;             // B1 口径状态复位（单会话互斥下理论冗余，防御式）
        _blockWaitTimeouts = 0;
        _lastResortSent = false;
        _lastRequestedPackH = 0xFF;
        _sameBlockRequests = 0;
        _terminalLostCandidate = false;
        _terminalLostConfirmed = false;
        int gap = chunkGapMs >= 0 ? chunkGapMs : _config.ChunkGapMs;

        _channel.FrameReceived += OnFrameReceived;
        try
        {
            MoveTo(OtaFsmState.StartSent, $"发开始帧：target={target} len={image.Length}B crc=0x{_crc:X4} sver={sver1}.{sver2}.{sver3}");
            await SendSessionAsync(OtaDcmd.Start, cancellationToken).ConfigureAwait(false);

            while (true)
            {
                if (State is OtaFsmState.Done or OtaFsmState.Failed or OtaFsmState.Aborted)
                    return BuildResult();

                var timeout = State switch
                {
                    OtaFsmState.StartSent => _config.StartTimeoutMs,
                    // PC-2（issues/08）：块等待锚点 = 本块首片发出 → 等待剩余时长 = deadline − now
                    OtaFsmState.AwaitingRequest => BlockWaitRemainingMs(),
                    OtaFsmState.Aborting => _config.StartTimeoutMs,
                    _ => 0
                };

                OtaEvent? ev;
                if (timeout > 0)
                {
                    ev = await WaitForEventAsync(timeout, cancellationToken).ConfigureAwait(false);
                    if (ev is null)
                    {
                        await OnTimeoutAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }
                else
                {
                    ev = await _events.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                }

                var result = await HandleAsync(ev.Value, abortAfterBlock, gap, cancellationToken).ConfigureAwait(false);
                if (result != null)
                    return result;
            }
        }
        finally
        {
            _channel.FrameReceived -= OnFrameReceived;
        }
    }

    /// <summary>主动退出：发 `DCMD=00` 并等回显（外部取消令牌触发时由循环收敛）</summary>
    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        _aborted = true;
        if (State is OtaFsmState.Done or OtaFsmState.Failed or OtaFsmState.Aborted or OtaFsmState.Idle)
        {
            State = OtaFsmState.Aborted;
            return;
        }

        MoveTo(OtaFsmState.Aborting, "用户取消：发 DCMD=00 退出");
        await SendSessionAsync(OtaDcmd.Exit, cancellationToken).ConfigureAwait(false);
    }

    // ================= 事件处理 =================

    private async Task<OtaResult?> HandleAsync(OtaEvent ev, int abortAfterBlock, int gap,
        CancellationToken ct)
    {
        switch (ev.Kind)
        {
            case OtaEventKind.SessionAck:
                return OnSessionAck(ev.Dcmd);

            case OtaEventKind.BlockRequest:
                return await OnBlockRequestAsync(ev.PackH, abortAfterBlock, gap, ct).ConfigureAwait(false);

            case OtaEventKind.Progress:
                return OnProgress(ev.Dcmd, ev.PackH);

            default:
                return null;
        }
    }

    private OtaResult? OnSessionAck(byte dcmd)
    {
        if (State == OtaFsmState.Aborting)
        {
            if (dcmd == OtaDcmd.Exit)
            {
                _outcome = OtaOutcome.Aborted;
                MoveTo(OtaFsmState.Aborted, "收到退出回显 DCMD=00");
                return BuildResult();
            }
            return null;   // 忽略其它应答，继续等回显（超时兜底）
        }

        if (State != OtaFsmState.StartSent)
            return null;   // 迟到的应答，忽略

        switch (dcmd)
        {
            case OtaDcmd.Start:
                // 14号 方案乙：设备答 ALLOW（而非同版本 skip）= 真重启会话 → 终态丢失推断作废
                _terminalLostCandidate = false;
                MoveTo(OtaFsmState.AwaitingRequest, "设备允许传输（DCMD=01）");
                return null;

            case OtaDcmd.Skip:
                if (_terminalLostCandidate)
                {
                    // 14号 方案乙 §5：满块 + R2 同版本 skip ⇒ 等价终态 DCMD=02
                    //（设备只会在已跑新固件时答同版本 skip），不引入误报成功新可能
                    _terminalLostConfirmed = true;
                    _outcome = OtaOutcome.Succeeded;
                    MoveTo(OtaFsmState.Done, "同版本 skip（DCMD=02）+ 满块 ⇒ 成功（终态丢失，R2 确认）");
                }
                else
                {
                    _outcome = OtaOutcome.Skipped;
                    MoveTo(OtaFsmState.Done, "已有相同固件（DCMD=02，跳过）");
                }
                return BuildResult();

            default:
                MoveTo(OtaFsmState.Failed, $"设备拒绝：DCMD=0x{dcmd:X2}");
                return BuildResult();
        }
    }

    private async Task<OtaResult?> OnBlockRequestAsync(byte packH, int abortAfterBlock, int gap,
        CancellationToken ct)
    {
        // R5：越界 PACKH → 忽略 + 告警，禁止补零糊弄
        if (packH >= _totalBlocks)
        {
            Warning?.Invoke(this, $"越界块请求 PACKH=0x{packH:X2}（总 {_totalBlocks} 块）→ 忽略（R5）");
            return null;
        }

        if (State == OtaFsmState.Aborting)
        {
            Warning?.Invoke(this, $"退出中收到块请求 PACKH=0x{packH:X2} → 忽略");
            return null;
        }

        // PC-6（issues/05 §2.3-3 / issues/08）：B1 下 `0x01(DCMD=01)` 应答与首个 `0x0A` 几乎同时到达，
        // 甚至 `0x0A` 先被处理 —— 设备只有 ALLOW 后才会拉块 ⇒ 视为**隐式允许**，
        // 先入 `AwaitingRequest` 再处理拉块（迟到的 DCMD=01 按既有"迟到应答"路径忽略）
        if (State == OtaFsmState.StartSent)
        {
            _startRetry = 0;
            MoveTo(OtaFsmState.AwaitingRequest, "0x0A 先于开始应答被处理 → 隐式允许（设备仅 ALLOW 后拉块）");
        }

        // PC-4（issues/05 §1-G / issues/08）：同块反复重查 → **仅告警**（保险计数，不作失败信号；
        // 块级失败唯一权威 = 设备终态 0x20 DCMD=03）
        if (packH == _lastRequestedPackH)
        {
            _sameBlockRequests++;
            if (_sameBlockRequests == _config.OtaMaxRetry + 1)
                Warning?.Invoke(this,
                    $"块 0x{packH:X2} 已被重查 {_sameBlockRequests - 1} 次（超保险阈值 {_config.OtaMaxRetry}）→ " +
                    "仅告警：链路质量差；失败以设备终态 0x20 DCMD=03 为准（不作失败信号）");
        }
        else
        {
            _lastRequestedPackH = packH;
            _sameBlockRequests = 1;
        }

        // R1：块级重查（含 Sending1K 被打断）→ 当前块作废，按新 PACKH 重发
        if (State == OtaFsmState.Sending1K)
            Warning?.Invoke(this, $"发片被打断：设备重查 PACKH=0x{packH:X2} → 该块作废重发（R1）");

        _startRetry = 0;    // 收到有效请求即清零重试计数
        _blockWaitTimeouts = 0;
        MoveTo(OtaFsmState.Sending1K, $"发块 PACKH=0x{packH:X2}（22×48B，片间 {gap}ms）");
        // PC-2（issues/08）：块等待锚点 = **本块首片发出**时刻（非末片发完，二者差 ~80ms）
        _blockDeadline = _clock.UtcNow.AddMilliseconds(_config.BlockTimeoutMs);

        for (byte packL = 0; packL < ChunksPerBlock; packL++)
        {
            // 发片途中也要响应新事件（R1 重查 / 0x20 终态）
            if (_events.Reader.TryRead(out var ev))
            {
                if (ev.Kind == OtaEventKind.BlockRequest)
                {
                    Warning?.Invoke(this, $"片间收到新块请求 PACKH=0x{ev.PackH:X2} → 当前块作废（R1）");
                    return await OnBlockRequestAsync(ev.PackH, abortAfterBlock, gap, ct).ConfigureAwait(false);
                }

                var early = await HandleAsync(ev, abortAfterBlock, gap, ct).ConfigureAwait(false);
                if (early != null)
                    return early;
            }

            await SendChunkAsync(packH, packL, gap, ct).ConfigureAwait(false);
        }

        _blocksDone = Math.Max(_blocksDone, packH + 1);

        // 14号 方案乙-1：满块（自算 100%）→ 末块后改入"等终态窗"（≥5s，覆盖设备 VERIFY+activate+复位；
        // 设备侧烧写对我不可见，等的是 0x20 终态而非下一块请求），不再按块级 1.5s 节奏
        if (_blocksDone >= _totalBlocks)
        {
            MoveTo(OtaFsmState.AwaitingRequest,
                $"全部 {_totalBlocks} 块发完 → 等终态窗 {_config.OtaTerminalWaitMs}ms（0x20 DCMD=02/03）");
            _blockDeadline = _clock.UtcNow.AddMilliseconds(_config.OtaTerminalWaitMs);
        }
        else
        {
            MoveTo(OtaFsmState.AwaitingRequest, $"块 PACKH=0x{packH:X2} 22 片发完");
        }
        RaiseProgress();

        if (_aborted || (abortAfterBlock >= 0 && _blocksDone >= abortAfterBlock))
        {
            if (!_aborted)
                Warning?.Invoke(this, $"到达 abort-after-block={abortAfterBlock} → 主动退出");
            await CancelAsync(ct).ConfigureAwait(false);
        }

        return null;
    }

    private OtaResult? OnProgress(byte dcmd, byte packH)
    {
        switch (dcmd)
        {
            case 0x01:
                _devicePercent = packH;
                // 0x20(01) 进度 = 设备存活证据（B1 下"先 0x20 后 0x0A"顺序固定）→ 顺延等待 deadline，
                // 避免设备正忙着发下一块请求时被锚点判超时；
                // 只顺延不回缩（满块后等终态窗 5s > 块级 1.5s，进度帧不得压缩终态窗，issues-pencil/14）
                if (State == OtaFsmState.AwaitingRequest)
                {
                    var extended = _clock.UtcNow.AddMilliseconds(_config.BlockTimeoutMs);
                    if (extended > _blockDeadline)
                        _blockDeadline = extended;
                }
                RaiseProgress();
                return null;

            case 0x02:
                _outcome = OtaOutcome.Succeeded;
                MoveTo(OtaFsmState.Done, "设备报完成（0x20 DCMD=02）");
                return BuildResult();

            case 0x03:
                MoveTo(OtaFsmState.Failed, $"设备报失败：{OtaFailReason.Describe(packH)}（0x20 DCMD=03）");
                return BuildResult();

            default:
                Warning?.Invoke(this, $"未知进度 DCMD=0x{dcmd:X2} → 忽略");
                return null;
        }
    }

    private async Task OnTimeoutAsync(CancellationToken ct)
    {
        switch (State)
        {
            case OtaFsmState.StartSent:
                if (++_startRetry > _config.OtaMaxRetry)
                {
                    MoveTo(OtaFsmState.Failed, $"开始帧无应答（重试 {_config.OtaMaxRetry} 次耗尽，R2）");
                    return;
                }
                Warning?.Invoke(this, $"开始帧超时（第 {_startRetry} 次）→ 重发（R2）");
                await SendSessionAsync(OtaDcmd.Start, ct).ConfigureAwait(false);
                break;

            case OtaFsmState.AwaitingRequest:
                // 14号 方案乙-2：满块后等终态窗超时 → 不直接 Failed，先 R2 重发开始帧确认——
                // 设备已跑新固件才会答同版本 skip ⇒ 满块 + skip 等价终态 DCMD=02（成功（终态丢失））；
                // R2 已用（无法再重启会话）或后续非 skip → Failed（不悬挂，红线 5）。
                if (_blocksDone >= _totalBlocks)
                {
                    if (!_lastResortSent)
                    {
                        _lastResortSent = true;
                        _terminalLostCandidate = true;
                        Warning?.Invoke(this,
                            $"等终态窗超时（{_config.OtaTerminalWaitMs}ms）未收 0x20 DCMD=02/03 " +
                            $"（满块 {_blocksDone}/{_totalBlocks}）→ R2 重发开始帧确认：" +
                            "同版本 skip ⇒ 成功（终态丢失）；其它 ⇒ 失败");
                        _startRetry = 0;
                        MoveTo(OtaFsmState.StartSent, "终态窗超时：R2 重发开始帧确认终态");
                        await SendSessionAsync(OtaDcmd.Start, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        // 同版本语义注意级（非错误）：同版本重刷 + 终态竞态下设备可能实际已升级成功
                        //（OTA升级固件级校验议题：skip 宏关闭后同版本受理整包重刷，终态丢失即撞此形态，
                        // issues-pencil/14 方案甲根治前的已知窗口）——失败照报，但提示以读版本/hash 复核为准
                        Warning?.Invoke(this,
                            "若本次为同版本包（SVER 与设备相同）：设备可能实际已升级成功（终态帧竞态丢失）——" +
                            "请用 identify / company hash 复核设备实际版本后再决定是否重刷");
                        MoveTo(OtaFsmState.Failed,
                            $"等终态窗超时且 R2 已用（满块 {_blocksDone}/{_totalBlocks}，终态帧丢失无法确认，issues-pencil/14）");
                    }
                    break;
                }

                // R2（B1 冻结口径，issues/05 §1-B / issues/08 PC-3）：超时**首选继续等待**——
                // 设备可能仍在 ~800ms 重查窗内（1500ms 锚点 > 重查窗，本不应超时；超时多为链路慢）。
                // "重发开始帧"会重启会话、与设备 FSM 分叉 ⇒ 降为**最后手段且只用一次**；
                // 最后手段后仍耗尽 → Failed（不悬挂，红线 5）。
                _blockWaitTimeouts++;
                if (_blockWaitTimeouts <= _config.OtaMaxRetry)
                {
                    Warning?.Invoke(this,
                        $"块等待超时（第 {_blockWaitTimeouts}/{_config.OtaMaxRetry} 轮续等；" +
                        $"锚点=块首片发出 {_config.BlockTimeoutMs}ms > 笔端重查窗 ≈800ms）→ 继续等待（不重发开始帧）");
                    _blockDeadline = _clock.UtcNow.AddMilliseconds(_config.BlockTimeoutMs);
                }
                else if (!_lastResortSent)
                {
                    _lastResortSent = true;
                    Warning?.Invoke(this,
                        $"块等待续等 {_config.OtaMaxRetry} 轮耗尽 → 最后手段：重发开始帧重启会话（R2，B1 冻结口径；本次会话仅此一次）");
                    _startRetry = 0;
                    _blockWaitTimeouts = 0;
                    MoveTo(OtaFsmState.StartSent, "块等待耗尽：最后手段重发开始帧");
                    await SendSessionAsync(OtaDcmd.Start, ct).ConfigureAwait(false);
                }
                else
                {
                    MoveTo(OtaFsmState.Failed,
                        $"块等待续等耗尽且最后手段已用（{_config.BlockTimeoutMs}ms × {_config.OtaMaxRetry + 1} × 2 轮，R2）");
                }
                break;

            case OtaFsmState.Aborting:
                if (++_exitAttempts > _config.OtaMaxRetry)
                {
                    MoveTo(OtaFsmState.Failed, "退出回显超时（R4：不悬挂，收敛为失败）");
                    return;
                }
                Warning?.Invoke(this, $"退出回显超时（第 {_exitAttempts} 次）→ 重发 DCMD=00");
                await SendSessionAsync(OtaDcmd.Exit, ct).ConfigureAwait(false);
                break;
        }
    }

    // ================= IO（仅经接口） =================

    /// <summary>
    /// PC-2（issues/08）：`AwaitingRequest` 的等待剩余时长 = 块锚点 deadline − now。
    /// 尚未发过任何块（等设备首拉）时取全额 <see cref="CompanyConfig.BlockTimeoutMs"/>；
    /// 满块后锚点改挂**等终态窗** <see cref="CompanyConfig.OtaTerminalWaitMs"/>（issues-pencil/14）。
    /// ⚠️ 恒 ≥1ms：主循环以 `timeout == 0` 区分"无限等待"（Sending1K 片间读事件）——
    /// deadline 已过时须以 1ms 走限时路径立即收敛到超时分支，不能落入无限阻塞。
    /// </summary>
    private int BlockWaitRemainingMs()
    {
        if (_blockDeadline == DateTime.MaxValue)
            return _config.BlockTimeoutMs;
        var remaining = (int)(_blockDeadline - _clock.UtcNow).TotalMilliseconds;
        return remaining < 1 ? 1 : remaining;
    }

    private Task SendSessionAsync(byte dcmd, CancellationToken ct) =>
        _channel.SendAsync(_codec.EncodeOtaSession(_target, dcmd, _hver, _sver1, _sver2, _sver3, _crc, (uint)_image.Length), ct);

    private async Task SendChunkAsync(byte packH, byte packL, int gap, CancellationToken ct)
    {
        int offset = (packH << 10) + packL * BytesPerChunk;

        var slice = new byte[BytesPerChunk];    // 末片截断 + 补零（报文恒 48B/60B）
        int copy = Math.Min(BytesPerChunk, Math.Max(0, _image.Length - offset));
        if (copy > 0)
            _image.AsSpan(offset, copy).CopyTo(slice);

        await _channel.SendAsync(_codec.EncodeOtaChunk(_target, 0x00, packH, packL, slice), ct).ConfigureAwait(false);

        if (packL < ChunksPerBlock - 1)
            await _clock.DelayAsync(gap, ct).ConfigureAwait(false);
    }

    private void OnFrameReceived(object? sender, byte[] frame)
    {
        if (!CompanyFrame.TryParse(frame, out var baseCmd, out var cmdType, out var data))
            return;
        if (baseCmd != CompanyFrame.BaseCmdResponse || cmdType != CompanyCmd.Ota)
            return;

        if (_codec.TryParseOtaSessionResp(data, out _, out var dcmd) && data.Length >= 3 && data[0] == 0x01)
        {
            _events.Writer.TryWrite(new OtaEvent(OtaEventKind.SessionAck, Dcmd: dcmd));
            return;
        }

        if (_codec.TryParseOtaBlockRequest(data, out var dt, out _, out var packH))
        {
            if (dt != (byte)_target)
            {
                Warning?.Invoke(this, $"块请求 DOWNTYPE=0x{dt:X2} ≠ 会话目标 0x{(byte)_target:X2} → 忽略（E23）");
                return;
            }
            _events.Writer.TryWrite(new OtaEvent(OtaEventKind.BlockRequest, PackH: packH));
            return;
        }

        if (_codec.TryParseOtaProgress(data, out var pdt, out var pdcmd, out var ppackH))
        {
            if (pdt != (byte)_target)
            {
                Warning?.Invoke(this, $"进度帧 DOWNTYPE=0x{pdt:X2} ≠ 会话目标 → 忽略（E23）");
                return;
            }
            _events.Writer.TryWrite(new OtaEvent(OtaEventKind.Progress, Dcmd: pdcmd, PackH: ppackH));
        }
    }

    /// <summary>限时取事件；超时返回 null。用 `WaitToReadAsync`（不消费）避免超时路径遗留的读取偷走下一事件</summary>
    private async Task<OtaEvent?> WaitForEventAsync(int timeoutMs, CancellationToken ct)
    {
        var wait = _events.Reader.WaitToReadAsync(ct).AsTask();
        var delay = _clock.DelayAsync(timeoutMs, ct);

        var done = await Task.WhenAny(wait, delay).ConfigureAwait(false);
        if (done != wait || !await wait.ConfigureAwait(false))
            return null;

        return _events.Reader.TryRead(out var ev) ? ev : null;
    }

    private void MoveTo(OtaFsmState next, string reason)
    {
        var from = State;
        State = next;
        Transitioned?.Invoke(this, new OtaTransition(from, next, reason));
    }

    private void RaiseProgress()
    {
        int percent = _totalBlocks == 0 ? 0 : _blocksDone * 100 / _totalBlocks;
        Progress?.Invoke(this, new OtaProgress(percent, _blocksDone, _totalBlocks, _devicePercent));
    }

    private OtaResult BuildResult() => _outcome switch
    {
        OtaOutcome.Succeeded => _terminalLostConfirmed
            ? new(OtaOutcome.Succeeded, 100, _blocksDone, _totalBlocks,
                $"成功（终态丢失）：{_blocksDone}/{_totalBlocks} 块全发 + R2 同版本确认（终态帧未达）",
                TerminalLost: true)
            : new(OtaOutcome.Succeeded, 100, _blocksDone, _totalBlocks,
                $"完成（{_blocksDone}/{_totalBlocks} 块）"),
        OtaOutcome.Skipped => new(OtaOutcome.Skipped, 0, _blocksDone, _totalBlocks,
            "跳过：已有相同固件（DCMD=02）"),
        OtaOutcome.Aborted => new(OtaOutcome.Aborted, _blocksDone * 100 / Math.Max(1, _totalBlocks),
            _blocksDone, _totalBlocks, "已退出（DCMD=00 回显确认）"),
        _ when State == OtaFsmState.Failed => new(OtaOutcome.Failed,
            _blocksDone * 100 / Math.Max(1, _totalBlocks), _blocksDone, _totalBlocks, "失败（详见转移原因）"),
        _ => new(OtaOutcome.None, 0, _blocksDone, _totalBlocks, State.ToString())
    };

    private readonly record struct OtaEvent(OtaEventKind Kind, byte Dcmd = 0, byte PackH = 0);

    private enum OtaEventKind
    {
        SessionAck,
        BlockRequest,
        Progress
    }
}
