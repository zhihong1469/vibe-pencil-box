using Microsoft.Extensions.Logging;
using PencilBox.Core.Communication;
using PencilBox.Core.Protocol.Company;

namespace PencilBox.Infrastructure.Serial;

/// <summary>
/// 串口命令通道（PC ↔ 笔端，工厂直连）：公司 V0.9 **逻辑帧** ←→ 变长字节流。
///
/// 形态职责（骨架 §4.1 / 上位机 `01 §②`）：**不补零**——`LEN` 是唯一定帧字段；
/// 上行由 <see cref="CompanyFrameReader"/> 装配（找 `F5 AA AA` → 取 `LEN` → 收满 `LEN+4`），
/// 并按**帧间隔超时**弃半帧重同步（对方中途断流时不悬挂）。
/// 本类**不含命令语义**。
/// </summary>
public sealed class SerialCommandChannel : ICommandChannel
{
    private readonly ISerialTransport _transport;
    private readonly CompanyConfig _config;
    private readonly ILogger<SerialCommandChannel> _logger;
    private readonly CompanyFrameReader _reader = new();
    private readonly Timer _gapTimer;

    private long _lastDataTicks;

    public SerialCommandChannel(ISerialTransport transport, CompanyConfig config, ILogger<SerialCommandChannel> logger)
    {
        _transport = transport;
        _config = config;
        _logger = logger;

        _transport.DataReceived += OnDataReceived;

        // 帧间隔兜底：半帧滞留超过阈值即弃帧重同步（骨架 §4.1）
        _gapTimer = new Timer(_ => CheckFrameGap(), null, dueTime: 50, period: 50);
    }

    public ChannelKind Kind => ChannelKind.Serial;

    public bool IsOpen => _transport.IsConnected;

    public string Describe => IsOpen
        ? $"串口 {_transport.CurrentPortName} @ {_transport.CurrentSettings?.BaudRate}"
        : "串口未打开";

    public event EventHandler<byte[]>? FrameReceived;

    public Task SendAsync(ReadOnlySpan<byte> logicalFrame, CancellationToken cancellationToken = default)
    {
        if (!IsOpen)
            throw new InvalidOperationException($"串口命令通道不可用：{Describe}");

        // 串口形态**不补零**：LEN 是唯一定帧字段
        var bytes = logicalFrame.ToArray();

        _logger.LogDebug("串口通道发逻辑帧 {Len}B（不补零）：{Hex}", bytes.Length, Convert.ToHexString(bytes));

        return _transport.SendAsync(bytes, cancellationToken);
    }

    private void OnDataReceived(object? sender, SerialDataReceivedEventArgs e)
    {
        _lastDataTicks = Environment.TickCount64;

        foreach (var frame in _reader.Append(e.Data))
            FrameReceived?.Invoke(this, frame);
    }

    private void CheckFrameGap()
    {
        if (_reader.PendingLength == 0)
            return;
        if (Environment.TickCount64 - _lastDataTicks < _config.SerialFrameGapMs)
            return;

        _logger.LogWarning("串口半帧滞留超 {Gap}ms（{Pending}B）→ 弃帧重同步",
            _config.SerialFrameGapMs, _reader.PendingLength);
        _reader.Reset();
    }

    /// <summary>只退订，不释放 <see cref="ISerialTransport"/>（该传输层为共享单例）</summary>
    public ValueTask DisposeAsync()
    {
        _gapTimer.Dispose();
        _transport.DataReceived -= OnDataReceived;
        return ValueTask.CompletedTask;
    }
}
