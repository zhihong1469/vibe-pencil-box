using Microsoft.Extensions.Logging;
using PencilBox.Core.Communication;
using PencilBox.Core.Protocol.Company;

namespace PencilBox.Infrastructure.Hid;

/// <summary>
/// USB HID 命令通道（PC ↔ 基站）：公司 V0.9 **逻辑帧** ←→ 64B 无 RID 报告。
///
/// 形态职责（骨架 §4.1）：**写侧补零到 64B**（前导 `0x00` 虚拟报告 ID 由传输层补齐）；
/// **读侧按 `LEN` 去补零**（传输层已剥前导 `0x00`）。
/// 本类**不含命令语义**（不认 CMDTYPE、不重试）——那是 FSM 的事。
/// </summary>
public sealed class HidCommandChannel : ICommandChannel
{
    private readonly IHidTransport _transport;
    private readonly ILogger<HidCommandChannel> _logger;

    public HidCommandChannel(IHidTransport transport, ILogger<HidCommandChannel> logger)
    {
        _transport = transport;
        _logger = logger;
        _transport.ReportReceived += OnReportReceived;
    }

    public ChannelKind Kind => ChannelKind.Hid;

    public bool IsOpen => _transport.IsConnected && _transport.IsNoReportIdDevice;

    public string Describe => IsOpen
        ? $"USB 0xFF02 EP4（{_transport.CurrentDevice?.ProductName}）"
        : "USB 未打开（需 0xFF02 无 RID 命令通道）";

    public event EventHandler<byte[]>? FrameReceived;

    public Task SendAsync(ReadOnlySpan<byte> logicalFrame, CancellationToken cancellationToken = default)
    {
        if (!IsOpen)
            throw new InvalidOperationException($"USB 命令通道不可用：{Describe}");

        if (logicalFrame.Length > 64)
            throw new ArgumentException($"逻辑帧超 64B（{logicalFrame.Length}）：USB 定长承载无法容纳", nameof(logicalFrame));

        // 定长承载：补零到 64B（前导 0x00 虚拟报告 ID 由传输层补齐）
        var report = new byte[64];
        logicalFrame.CopyTo(report);

        _logger.LogDebug("USB 通道发逻辑帧 {Len}B（补零至 64B）：{Hex}",
            logicalFrame.Length, Convert.ToHexString(logicalFrame));

        return _transport.SendReportAsync(0, report, cancellationToken);
    }

    private void OnReportReceived(object? sender, HidReportReceivedEventArgs e)
    {
        if (!_transport.IsNoReportIdDevice)
            return;

        if (!CompanyFrame.TryTrimToLogical(e.Data, out var logical))
        {
            _logger.LogDebug("丢弃非 F5 帧（{Len}B）", e.Data.Length);
            return;
        }

        FrameReceived?.Invoke(this, logical);
    }

    /// <summary>只退订，不释放 <see cref="IHidTransport"/>（该传输层为共享单例，由容器统一释放）</summary>
    public ValueTask DisposeAsync()
    {
        _transport.ReportReceived -= OnReportReceived;
        return ValueTask.CompletedTask;
    }
}
