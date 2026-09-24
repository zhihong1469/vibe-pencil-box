using Microsoft.Extensions.Logging.Abstractions;
using PencilBox.Core.Communication;
using PencilBox.Core.SerialScope;
using PencilBox.Infrastructure.Serial;

namespace PencilBox.Tests;

/// <summary>
/// 串口抓日志会话 × Hub 收尾回归（06 M1 验收"双口采集正常停止卡死"）：
/// 停止后会话必须**收敛**（返回 0）且 **hub 引用归零**（两个口都物理关闭、读侧不泄漏）。
/// IO 用 Fake（零真实串口）；传输层"读未收尾 → 后台关句柄"路径依赖真实驱动行为，离线不可模拟，
/// 由真机复测覆盖（06 §8.5）。
/// </summary>
public class SerialLogSessionTests
{
    /// <summary>最小假 IO（同 SerialPortHubTests 夹具，Infrastructure 侧重建一份避免跨工程共享）</summary>
    private sealed class FakePortIo : ISerialPortIo
    {
        public bool IsOpen { get; private set; }
        public event EventHandler<ReadOnlyMemory<byte>>? Received;

        public Task<bool> OpenAsync(string portName, int baudRate, CancellationToken cancellationToken = default)
        {
            IsOpen = true;
            return Task.FromResult(true);
        }

        public Task CloseAsync()
        {
            IsOpen = false;
            return Task.CompletedTask;
        }

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    /// <summary>只负责枚举的假 ISerialTransport（会话用它解析端口清单，不收发）</summary>
    private sealed class FakeEnumerator : ISerialTransport
    {
        public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged { add { } remove { } }
        public event EventHandler<SerialDataReceivedEventArgs>? DataReceived { add { } remove { } }
        public bool IsConnected => false;
        public string? CurrentPortName => null;
        public SerialPortSettings? CurrentSettings => null;

        public Task<IReadOnlyList<SerialPortInfo>> EnumeratePortsAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyList<SerialPortInfo> ports = new[]
            {
                new SerialPortInfo { PortName = "COM20", Description = "USB-SERIAL CH340" },
                new SerialPortInfo { PortName = "COM6", Description = "JLVirtualJtagSerial Device (COM6)" }
            };
            return Task.FromResult(ports);
        }

        public Task<bool> ConnectAsync(string portName, SerialPortSettings settings,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendAsync(byte[] data, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SendTextAsync(string text, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SendHexAsync(string hexString, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static (SerialLogSession session, SerialPortHub hub) NewSessionShared()
    {
        var hub = new SerialPortHub(() => new FakePortIo());
        var session = new SerialLogSession(new FakeEnumerator(), NullLoggerFactory.Instance, hub);
        return (session, hub);
    }

    [Fact]
    public async Task TwoPorts_StopNormal_SessionConverges_And_ReleasesBothPorts()
    {
        var (session, hub) = NewSessionShared();
        var logDir = Path.Combine(Path.GetTempPath(), "pencilbox-tests", Guid.NewGuid().ToString("N"));

        var request = new SerialLogSessionRequest
        {
            Ports =
            [
                new SerialScopePortSpec { Name = "PENCIL", Port = "COM20", BaudRate = 1_000_000 },
                new SerialScopePortSpec { Name = "DONGLE", Port = "COM6", BaudRate = 115_200 }
            ],
            LogDir = logDir,
            ClearOnStart = false,
            Jsonl = false
        };

        using var cts = new CancellationTokenSource(500);   // 模拟用户点"停止"
        var code = await session.RunAsync(request, cts.Token);

        Assert.Equal(0, code);                              // 收敛（卡死 = 永不返回）
        Assert.False(hub.IsPortOpen("COM20"));              // 修复前：M1 泄漏 ⇒ 口一直开着
        Assert.False(hub.IsPortOpen("COM6"));
    }

    [Fact]
    public async Task Reader_StopThenDispose_ReleasesHubReference()
    {
        var hub = new SerialPortHub(() => new FakePortIo());
        var reader = new SerialLogReader("PENCIL", "COM20", 115200, decodeFrames: false,
            NullLoggerFactory.Instance, hub);

        Assert.True(await reader.StartAsync());             // 独占经 hub 开口
        Assert.True(hub.IsPortOpen("COM20"));

        await reader.StopAsync();
        await reader.DisposeAsync();

        // M1 回归：StopAsync 曾把 _tapped 置 false ⇒ DisposeAsync 跳过 hub.CloseAsync ⇒ 泄漏
        Assert.False(hub.IsPortOpen("COM20"));
        Assert.Null(hub.OwnersOf("COM20"));
    }

    [Fact]
    public async Task Reader_SecondSession_AfterRelease_OpensExclusivelyAgain()
    {
        var hub = new SerialPortHub(() => new FakePortIo());
        var logFactory = NullLoggerFactory.Instance;

        var first = new SerialLogReader("PENCIL", "COM20", 115200, false, logFactory, hub);
        await first.StartAsync();
        await first.StopAsync();
        await first.DisposeAsync();

        // 停止后重开：不得残留第一轮引用（否则第二轮被迫走旁路、波特率被旧值锁死）
        var second = new SerialLogReader("PENCIL", "COM20", 2_000_000, false, logFactory, hub);
        Assert.True(await second.StartAsync());
        Assert.Equal(2_000_000, hub.BaudRateOf("COM20"));
        await second.StopAsync();
        await second.DisposeAsync();
        Assert.False(hub.IsPortOpen("COM20"));
    }
}
