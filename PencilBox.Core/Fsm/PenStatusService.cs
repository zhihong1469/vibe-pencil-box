using PencilBox.Core.Communication;
using PencilBox.Core.Protocol.Company;

namespace PencilBox.Core.Fsm;

/// <summary>
/// `0x53` 状态上行**会话服务**（协议补充 05）——拉推双向的 PC 侧封装。
///
/// 单帧单事务、无状态机（与 <see cref="CompanyPenCmdService"/> 同款轻服务）：
/// 拉取 = 发 `61 01 53 01` 等应答（基站本地槽缓存即答，不打扰笔端）；
/// 监听 = 只等 `E1 01 53` 推送（推管实时；推送丢失下一帧自愈，超时静默返回 null）。
/// **仅 USB 基站轨**：基站"只搬运不解析"（BYTE2 + 状态槽原样上行），笔端串口轨零改动不实现。
/// </summary>
public sealed class CompanyPenStatusService
{
    private readonly ICompanyCodec _codec;
    private readonly ICommandChannel _channel;
    private readonly IClock _clock;
    private readonly CompanyConfig _config;

    public CompanyPenStatusService(ICompanyCodec codec, ICommandChannel channel, IClock clock, CompanyConfig config)
    {
        _codec = codec;
        _channel = channel;
        _clock = clock;
        _config = config;
    }

    /// <summary>底层通道是否就绪（UI 据此控制按钮可用态/提示先连接）。</summary>
    public bool IsOpen => _channel.IsOpen;

    /// <summary>
    /// 拉取状态快照（`61` 拉）：基站缓存即答。
    /// 无应答（旧固件对未知 CMDTYPE 静默丢弃）或结构非法 → null；`STATUS=02` = 笔未连接（合法应答）。
    /// </summary>
    public async Task<PenStatusSnapshot?> PullAsync(CancellationToken cancellationToken = default)
    {
        var requester = new CommandRequester(_clock, _config);
        var resp = await requester.RequestAsync(_channel, _codec.EncodePenStatusPull(),
            CompanyCmd.PenStatus, cancellationToken).ConfigureAwait(false);

        return resp != null && _codec.TryParsePenStatusResp(resp, out var snapshot) ? snapshot : null;
    }

    /// <summary>
    /// 监听一条 `E1` 推送（连笔全量 / 槽变化 / 断连 `STATUS=02`）；窗口内无推送 → null。
    /// </summary>
    public async Task<PenStatusSnapshot?> ListenAsync(int timeoutMs, CancellationToken cancellationToken = default)
    {
        var requester = new CommandRequester(_clock, _config);
        var resp = await requester.ListenAsync(_channel, CompanyCmd.PenStatus, timeoutMs, cancellationToken)
            .ConfigureAwait(false);

        return resp != null && _codec.TryParsePenStatusResp(resp, out var snapshot) ? snapshot : null;
    }
}
