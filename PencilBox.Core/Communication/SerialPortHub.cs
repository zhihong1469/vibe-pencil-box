namespace PencilBox.Core.Communication;

/// <summary>
/// <see cref="ISerialPortHub"/> 的纯逻辑实现（06 M1）：每物理口一个 <see cref="ISerialPortIo"/> 实例，
/// 引用计数按 (端口, 参与者)，字节广播给全部订阅者。IO 经工厂注入 ⇒ 全量单测（见 SerialPortHubTests）。
/// </summary>
public sealed class SerialPortHub : ISerialPortHub
{
    private readonly Func<ISerialPortIo> _ioFactory;

    /// <summary>开口/关口串行化（物理打开耗时，串行化后状态机简单可判）</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>口名（OrdinalIgnoreCase）→ 该口的句柄/参与者/订阅</summary>
    private readonly Dictionary<string, PortEntry> _ports = new(StringComparer.OrdinalIgnoreCase);

    private sealed class PortEntry
    {
        public required ISerialPortIo Io { get; init; }
        public required int BaudRate { get; init; }
        public HashSet<string> Owners { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<Subscription> Subscriptions { get; } = new();
    }

    private sealed class Subscription
    {
        public required Action<ReadOnlyMemory<byte>> OnReceived { get; init; }
        public Action<ReadOnlyMemory<byte>>? OnWritten { get; init; }
        public PortEntry Entry { get; set; } = null!;
    }

    public SerialPortHub(Func<ISerialPortIo> ioFactory) => _ioFactory = ioFactory;

    public bool IsPortOpen(string portName)
    {
        lock (_ports)
            return _ports.TryGetValue(portName, out var e) && e.Io.IsOpen;
    }

    public int? BaudRateOf(string portName)
    {
        lock (_ports)
            return _ports.TryGetValue(portName, out var e) ? e.BaudRate : null;
    }

    public string? OwnersOf(string portName)
    {
        lock (_ports)
            return _ports.TryGetValue(portName, out var e) ? string.Join(", ", e.Owners) : null;
    }

    public async Task<bool> OpenAsync(string portName, int baudRate, string owner,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PortEntry? existing;
            lock (_ports)
                _ports.TryGetValue(portName, out existing);

            // 已打开：校波特率（同口同时只能一个波特率，06 §4.3）→ 幂等加引用
            if (existing != null)
            {
                if (existing.BaudRate != baudRate)
                    throw new SerialPortConflictException(portName, existing.BaudRate, baudRate,
                        string.Join(", ", existing.Owners));

                lock (_ports)
                    existing.Owners.Add(owner);
                return true;
            }

            // 未打开：建句柄 → 物理打开 → 注册
            var io = _ioFactory();
            if (!await io.OpenAsync(portName, baudRate, cancellationToken).ConfigureAwait(false))
                return false;

            var entry = new PortEntry { Io = io, BaudRate = baudRate };
            entry.Owners.Add(owner);
            io.Received += (_, data) => BroadcastReceived(entry, data);

            lock (_ports)
                _ports[portName] = entry;

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CloseAsync(string portName, string owner)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ISerialPortIo? toClose = null;
            lock (_ports)
            {
                if (!_ports.TryGetValue(portName, out var entry))
                    return;

                entry.Owners.Remove(owner);

                if (entry.Owners.Count > 0)
                    return;   // 还有别的参与者在用：只减引用，不动物理口（06 M1 的核心语义）

                _ports.Remove(portName);
                lock (entry.Subscriptions)
                    entry.Subscriptions.Clear();   // 口要关了：订阅全部失效
                toClose = entry.Io;
            }

            // 物理关放在锁外（Close 内部会停读线程、等待收尾）
            if (toClose != null)
                await toClose.CloseAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SendAsync(string portName, ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        PortEntry? entry;
        lock (_ports)
            _ports.TryGetValue(portName, out entry);

        if (entry == null || !entry.Io.IsOpen)
            throw new InvalidOperationException($"串口 {portName} 未打开，无法发送");

        // 先广播写旁路（同步）：保证日志时间轴上 `↓` 帧先于设备应答 `↑`（06 §4.2 写旁路）
        Broadcast(entry, s => s.OnWritten, data);

        await entry.Io.SendAsync(data, cancellationToken).ConfigureAwait(false);
    }

    public IDisposable Subscribe(string portName,
        Action<ReadOnlyMemory<byte>> onReceived,
        Action<ReadOnlyMemory<byte>>? onWritten = null)
    {
        Subscription sub;

        lock (_ports)
        {
            if (!_ports.TryGetValue(portName, out var entry))
                throw new InvalidOperationException(
                    $"串口 {portName} 未打开，无法订阅（先 OpenAsync，或确认口仍被共享持有）");

            sub = new Subscription { OnReceived = onReceived, OnWritten = onWritten, Entry = entry };
            lock (entry.Subscriptions)
                entry.Subscriptions.Add(sub);
        }

        return new SubscriptionHandle(this, portName, sub);
    }

    private void BroadcastReceived(PortEntry entry, ReadOnlyMemory<byte> data)
        => Broadcast(entry, s => s.OnReceived, data);

    /// <summary>快照订阅逐个投递；单个订阅者抛异常不波及其他订阅者与读线程（隔离，06 §4.1）</summary>
    private static void Broadcast(PortEntry entry, Func<Subscription, Action<ReadOnlyMemory<byte>>?> select,
        ReadOnlyMemory<byte> data)
    {
        Subscription[] snapshot;
        lock (entry.Subscriptions)
            snapshot = entry.Subscriptions.ToArray();

        foreach (var sub in snapshot)
        {
            var handler = select(sub);
            if (handler == null)
                continue;

            try { handler(data); }
            catch { /* 单个旁路失败绝不波及端口数据流（调用方各自 try-catch 已是纪律，这里再兜一层） */ }
        }
    }

    private sealed class SubscriptionHandle(SerialPortHub hub, string portName, Subscription sub) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;

            lock (hub._ports)
            {
                if (hub._ports.TryGetValue(portName, out var entry))
                    lock (entry.Subscriptions)
                        entry.Subscriptions.Remove(sub);
                else
                    lock (sub.Entry.Subscriptions)
                        sub.Entry.Subscriptions.Remove(sub);   // 口已被移除（关了）——订阅列表本就已清，删一次兜底
            }
        }
    }
}
