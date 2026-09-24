using System.Text;
using Microsoft.Extensions.Logging;
using PencilBox.Core.Communication;

namespace PencilBox.Infrastructure.Serial;

/// <summary>
/// **命令面共享串口**（06 M1）：实现既有 <see cref="ISerialTransport"/> 接口，内部全部转 <see cref="ISerialPortHub"/>。
/// 对命令面（读 MAC `0x51` / OTA 串口轨 `0x16` / CLI `company`）零感替换 DI 里的 <see cref="SerialTransport"/> 单例——
///
/// - 同口被串口页日志（独占模式）先开也能复用（引用计数，不再互抢）；
/// - 口被共享持有时本类的 <see cref="DisconnectAsync"/> 只释放**自己**的引用，不再断掉日志会话的口；
/// - 所有发出字节经 hub 的写旁路入日志时间轴（`↓` 帧）。
/// </summary>
public sealed class SharedSerialTransport : ISerialTransport
{
    /// <summary>参与 hub 引用计数用的参与者名（命令面整体一个名，06 §4.3）</summary>
    private const string Owner = SerialPortOwners.Command;

    private readonly ISerialPortHub _hub;
    private readonly SerialTransport _enumerator;   // 仅用于枚举（自身永不连接）
    private readonly ILogger<SharedSerialTransport> _logger;

    private IDisposable? _subscription;

    /// <summary>当前持有的口名（null = 未开口）</summary>
    private string? _portName;

    private SerialPortSettings? _settings;

    public SharedSerialTransport(
        ISerialPortHub hub,
        SerialTransport enumerator,
        ILogger<SharedSerialTransport> logger)
    {
        _hub = hub;
        _enumerator = enumerator;
        _logger = logger;
    }

    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
    public event EventHandler<SerialDataReceivedEventArgs>? DataReceived;

    public bool IsConnected => _portName != null && _hub.IsPortOpen(_portName);

    public string? CurrentPortName => _portName;

    public SerialPortSettings? CurrentSettings => _settings;

    public Task<IReadOnlyList<SerialPortInfo>> EnumeratePortsAsync(
        CancellationToken cancellationToken = default)
        => _enumerator.EnumeratePortsAsync(cancellationToken);

    public async Task<bool> ConnectAsync(string portName, SerialPortSettings settings,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("打开共享串口 {Port} @ {Baud}（经 hub，参与者 {Owner}）",
            portName, settings.BaudRate, Owner);

        try
        {
            // 先释放旧口引用（异口切换：旧口若还有日志在用则保持开，06 M1 语义）
            await ReleaseAsync().ConfigureAwait(false);

            if (!await _hub.OpenAsync(portName, settings.BaudRate, Owner, cancellationToken)
                .ConfigureAwait(false))
            {
                var owners = _hub.OwnersOf(portName);
                _logger.LogWarning("共享串口 {Port} 打开失败{OwnerHint}", portName,
                    owners == null ? string.Empty : $"（口仍被 [{owners}] 持有？）");

                ConnectionStateChanged?.Invoke(this,
                    new ConnectionStateChangedEventArgs(false, null,
                        new InvalidOperationException($"打开串口失败：{portName}（被外部程序占用？口不存在？）")));
                return false;
            }
        }
        catch (SerialPortConflictException ex)
        {
            // 同口异波特率：点名占用者（06 §4.3），不静默改频
            _logger.LogWarning("{Message}", ex.Message);
            ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(false, null, ex));
            return false;
        }

        _portName = portName;
        _settings = settings;

        // 持久订阅：hub 的该口字节 → 本类 DataReceived（命令帧装配方零改动）
        _subscription = _hub.Subscribe(portName,
            data => DataReceived?.Invoke(this, new SerialDataReceivedEventArgs(data.ToArray())));

        ConnectionStateChanged?.Invoke(this,
            new ConnectionStateChangedEventArgs(true, new HidDeviceInfo
            {
                DevicePath = portName,
                ProductName = $"Serial {portName}（共享）"
            }));

        return true;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await ReleaseAsync().ConfigureAwait(false);
    }

    public async Task SendAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        if (_portName == null)
            throw new InvalidOperationException("Serial port not connected");

        await _hub.SendAsync(_portName, data, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Sent {Bytes} bytes to {Port}（经 hub，写旁路已入日志时间轴）", data.Length, _portName);
    }

    public async Task SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (text == null)
            return;

        await SendAsync(Encoding.UTF8.GetBytes(text), cancellationToken).ConfigureAwait(false);
    }

    public async Task SendHexAsync(string hexString, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(hexString))
            return;

        var cleaned = System.Text.RegularExpressions.Regex.Replace(hexString, @"[^0-9A-Fa-f]", "");
        if (cleaned.Length % 2 != 0)
            throw new ArgumentException("Hex string length must be even");

        var bytes = new byte[cleaned.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(cleaned.Substring(i * 2, 2), 16);

        await SendAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>释放本参与者的引用与订阅；通知断开</summary>
    private async Task ReleaseAsync()
    {
        if (_portName == null)
            return;

        var port = _portName;
        _portName = null;
        _settings = null;

        _subscription?.Dispose();
        _subscription = null;

        await _hub.CloseAsync(port, Owner).ConfigureAwait(false);

        ConnectionStateChanged?.Invoke(this,
            new ConnectionStateChangedEventArgs(false, new HidDeviceInfo
            {
                DevicePath = port,
                ProductName = $"Serial {port}"
            }));
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask(DisconnectAsync());
    }
}
