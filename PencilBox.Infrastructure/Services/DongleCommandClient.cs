using Microsoft.Extensions.Logging;
using PencilBox.Core.Communication;
using PencilBox.Core.Protocol;
using PencilBox.Core.Services;

namespace PencilBox.Infrastructure.Services;

/// <summary>
/// EP4 命令通道客户端：发帧 + 等 Dongle 应答 + 解析（一问一答事务）。
/// 复用传输层 ReportReceived 分发；按 应答通道 ch=0x10 + CMD 回显 匹配自己的挂起请求。
/// 与 DeviceService 的 Custom(0xFF) 事务互不干扰（各自只消费匹配自己语义的帧）。
/// </summary>
public class DongleCommandClient : IDongleCommandClient
{
    private readonly IHidTransport _transport;
    private readonly ILogger<DongleCommandClient> _logger;

    private readonly object _gate = new();
    private TaskCompletionSource<byte[]>? _pending;
    private byte _pendingCmd;

    public DongleCommandClient(IHidTransport transport, ILogger<DongleCommandClient> logger)
    {
        _transport = transport;
        _logger = logger;
        _transport.ReportReceived += OnReportReceived;
    }

    public async Task<DongleReply> SendCommandAsync(byte cmd, byte[]? data = null,
        int timeoutMs = 1500, CancellationToken ct = default)
    {
        if (!_transport.IsConnected)
            return DongleReply.Timeout();

        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_pending != null)
                return DongleReply.Timeout();   // 有请求在途，拒绝并发
            _pending = tcs;
            _pendingCmd = cmd;
        }

        try
        {
            var frame = CommandFrame.Build(HidChannel.PcToDongle, (CommandId)cmd, data);
            _logger.LogDebug("EP4 cmd 0x{cmd:X2} frame {Len}B", cmd, frame.Length);
            await _transport.SendReportAsync(0, frame, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EP4 send failed for cmd 0x{X2}", cmd);
            ClearPending(tcs);
            return DongleReply.Timeout();
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        byte[]? payload;
        try
        {
            payload = await tcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("EP4 cmd 0x{X2} timed out after {T}ms", cmd, timeoutMs);
            ClearPending(tcs);
            return DongleReply.Timeout();
        }
        finally
        {
            ClearPending(tcs);
        }

        if (payload == null || payload.Length == 0)
            return DongleReply.Timeout();
        return DongleReply.Ok(payload[0], payload.Length > 1 ? payload[1..] : Array.Empty<byte>());
    }

    private void OnReportReceived(object? sender, HidReportReceivedEventArgs e)
    {
        TaskCompletionSource<byte[]>? tcs = null;
        DecodedFrame? decoded = null;
        lock (_gate)
        {
            if (_pending == null)
                return;
            decoded = PencilBoxProtocol.ParseResponse(e.Data);
            if (decoded == null || decoded.Channel != HidChannel.DongleToPc)
                return;                                 // 非应答帧：忽略（留给其它订阅者）
            if ((byte)decoded.CommandId != _pendingCmd)
                return;                                 // CMD 不匹配：忽略（异步上行/其它）
            tcs = _pending;
            _pending = null;
            _pendingCmd = 0;
        }
        tcs?.TrySetResult(decoded!.Payload);            // Payload = [status][…]
    }

    private void ClearPending(TaskCompletionSource<byte[]> tcs)
    {
        lock (_gate)
        {
            if (_pending == tcs)
            {
                _pending = null;
                _pendingCmd = 0;
            }
        }
    }
}
