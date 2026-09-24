using PencilBox.Core.Communication;
using PencilBox.Core.Protocol.Company;

namespace PencilBox.Core.Fsm;

/// <summary>
/// 会话层原语：**发送并等待匹配的上行应答**（`BASECMD=0xE1` + 指定 `CMDTYPE`）。
///
/// 被"请求-应答"型会话共用（配对 FSM、CLI 的 `identify`/`pair read|write`），
/// 是"超时必有收敛"的落地处（红线 3：**不悬挂**）。
/// 不含命令语义（不解析载荷），也不绑定通道（通道按会话传入 —— `03 §2.1`）。
/// </summary>
public sealed class CommandRequester
{
    private readonly IClock _clock;
    private readonly CompanyConfig _config;

    public CommandRequester(IClock clock, CompanyConfig config)
    {
        _clock = clock;
        _config = config;
    }

    /// <summary>
    /// 发送 <paramref name="request"/> 并等待 `CMDTYPE` 的应答；超时按重发上限重发。
    /// 返回应答的 `DATA`（`CMDTYPE` 之后）；重发耗尽返回 null。
    /// <paramref name="timeoutMs"/>/<paramref name="maxRetry"/> 缺省取 <see cref="CompanyConfig"/>
    /// 通用值；`0x54` 信封会话传**专用值**（1000/2000ms + ≤3，pc-issues/03 P-3——必须 >
    /// 终结层最坏窗，风格同块级 1500ms）。
    /// </summary>
    /// <remarks>
    /// 单循环内**先挂监听再发送**（快应答不丢窗），发送后才做超时裁决——
    /// 若先建等待再发送（旧写法），同步完成时钟（离线假时钟）会让等待窗在发送前就
    /// 同步到期摘除监听；响应与超时同刻到达时也应以响应为准。
    /// </remarks>
    public async Task<byte[]?> RequestAsync(ICommandChannel channel, byte[] request, byte cmdType,
        CancellationToken cancellationToken = default, int? timeoutMs = null, int? maxRetry = null)
    {
        int timeout = timeoutMs ?? _config.ResponseTimeoutMs;
        int retries = maxRetry ?? _config.MaxRetry;

        for (int attempt = 0; attempt <= retries; attempt++)
        {
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

            void Handler(object? sender, byte[] frame)
            {
                if (!CompanyFrame.TryParse(frame, out var baseCmd, out var type, out var data))
                    return;
                if (baseCmd != CompanyFrame.BaseCmdResponse || type != cmdType)
                    return;
                tcs.TrySetResult(data);
            }

            channel.FrameReceived += Handler;
            try
            {
                await channel.SendAsync(request, cancellationToken).ConfigureAwait(false);

                var timeoutTask = _clock.DelayAsync(timeout, cancellationToken);
                await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);

                if (tcs.Task.IsCompleted)          // 响应先到/同刻到 → 以响应为准
                    return tcs.Task.Result;
            }
            finally
            {
                channel.FrameReceived -= Handler;
            }
        }

        return null;
    }

    /// <summary>只等（不发）：给定超时内等待匹配的上行帧，超时返回 null。</summary>
    public Task<byte[]?> ListenAsync(ICommandChannel channel, byte cmdType, int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? sender, byte[] frame)
        {
            if (!CompanyFrame.TryParse(frame, out var baseCmd, out var type, out var data))
                return;
            if (baseCmd != CompanyFrame.BaseCmdResponse || type != cmdType)
                return;
            tcs.TrySetResult(data);
        }

        channel.FrameReceived += Handler;

        return AwaitAndDetachAsync();

        async Task<byte[]?> AwaitAndDetachAsync()
        {
            try
            {
                if (tcs.Task.IsCompletedSuccessfully)
                    return tcs.Task.Result;

                var timeout = _clock.DelayAsync(timeoutMs, cancellationToken);
                var done = await Task.WhenAny(tcs.Task, timeout).ConfigureAwait(false);
                return done == tcs.Task ? tcs.Task.Result : null;
            }
            finally
            {
                channel.FrameReceived -= Handler;
            }
        }
    }
}
