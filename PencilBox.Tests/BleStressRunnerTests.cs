using PencilBox.Core.Ble;
using PencilBox.Core.Communication;

namespace PencilBox.Tests;

/// <summary>
/// <see cref="BleStressRunner"/> 编排逻辑单测（假 <see cref="IBleCentral"/>，零平台依赖）。
/// 判定口径：回合失败 = 建连失败 / 枚举失败 / 配对或退配失败；PASS = 失败回合 ≤ AllowFailures 且至少 1 回合成功。
/// </summary>
public class BleStressRunnerTests
{
    private static BleStressOptions Opts(int rounds = 3, int allowFailures = 0, bool pairCycle = false,
        bool discover = true) => new()
    {
        Rounds = rounds,
        AllowFailures = allowFailures,
        PairCycle = pairCycle,
        Discover = discover,
        GapSec = 0,          // 测试不等待
        HoldSec = 0
    };

    private static BleConnectRequest Req() => new() { MacDisplay = "AA:BB:CC:DD:EE:FF" };

    [Fact]
    public async Task AllRoundsPass_PassTrue_AndCountersCorrect()
    {
        var central = new FakeBleCentral(discovery: Discovery(5), mtu: 247);
        var s = await BleStressRunner.RunAsync(central, Req(), Opts(rounds: 3));

        Assert.True(s.Pass);
        Assert.Equal(3, s.TotalRounds);
        Assert.Equal(3, s.PassedRounds);
        Assert.Equal(0, s.FailedRounds);
        Assert.Equal(247, s.MinMtu);
        Assert.Equal(3, central.DiscoverCalls);
        Assert.Equal(3, central.DisconnectCalls);
        Assert.Equal(0, central.PairCalls);
    }

    [Fact]
    public async Task ConnectFailure_CountedAndReasonRecorded()
    {
        var central = new FakeBleCentral(discovery: Discovery(5));
        central.ConnectResults.Enqueue(BleConnectResult.Fail("平台返回 null"));

        var s = await BleStressRunner.RunAsync(central, Req(), Opts(rounds: 2));

        Assert.False(s.Pass);                       // 1 失败 > 允许 0
        Assert.Equal(1, s.FailedRounds);
        Assert.True(s.Errors.ContainsKey("connect: 平台返回 null"));
        // 失败回合也要断连兜底，且继续下一回合
        Assert.Equal(2, central.ConnectCalls);
        Assert.Equal(1, central.DisconnectCalls);
    }

    [Fact]
    public async Task AllowFailures_ToleratesConfiguredFailures()
    {
        var central = new FakeBleCentral(discovery: Discovery(5));
        central.ConnectResults.Enqueue(BleConnectResult.Fail("超时"));

        var s = await BleStressRunner.RunAsync(central, Req(), Opts(rounds: 2, allowFailures: 1));

        Assert.True(s.Pass);
        Assert.Equal(1, s.AllowedFailures);
        Assert.Equal(1, s.FailedRounds);
    }

    [Fact]
    public async Task DiscoverFailure_FailsRound()
    {
        var central = new FakeBleCentral(discovery: BleDiscoveryResult.Fail("枚举超时"));
        var s = await BleStressRunner.RunAsync(central, Req(), Opts(rounds: 1));

        Assert.False(s.Pass);
        Assert.True(s.Errors.Keys.Any(k => k.StartsWith("discover:")));
    }

    [Fact]
    public async Task NoDiscover_SkipsEnumeration()
    {
        var central = new FakeBleCentral(discovery: Discovery(5));
        var s = await BleStressRunner.RunAsync(central, Req(), Opts(rounds: 1, discover: false));

        Assert.True(s.Pass);
        Assert.Equal(0, central.DiscoverCalls);
        Assert.Equal(23, s.MinMtu);                // MTU 来自建连应答，与是否枚举无关（假件缺省 23）
    }

    [Fact]
    public async Task PairCycle_SuccessRecordsPairAndUnpair()
    {
        var central = new FakeBleCentral(discovery: Discovery(5));
        var s = await BleStressRunner.RunAsync(central, Req(), Opts(rounds: 2, pairCycle: true));

        Assert.True(s.Pass);
        Assert.Equal(2, central.PairCalls);
        Assert.Equal(2, central.UnpairCalls);
    }

    [Fact]
    public async Task PairRejected_FailsRound_AndSkipsUnpair()
    {
        var central = new FakeBleCentral(discovery: Discovery(5));
        central.NextPair = new BlePairResult { Ok = false, StatusText = "RejectedByHandler" };

        var s = await BleStressRunner.RunAsync(central, Req(), Opts(rounds: 1, pairCycle: true));

        Assert.False(s.Pass);
        Assert.True(s.Errors.Keys.Any(k => k.StartsWith("pair:")));
        Assert.Equal(1, central.PairCalls);
        Assert.Equal(0, central.UnpairCalls);      // 配对失败不再退配
    }

    [Fact]
    public async Task UnpairRejected_FailsRound()
    {
        var central = new FakeBleCentral(discovery: Discovery(5));
        central.NextUnpair = new BlePairResult { Ok = false, StatusText = "CouldNotRemoveDevice" };

        var s = await BleStressRunner.RunAsync(central, Req(), Opts(rounds: 1, pairCycle: true));

        Assert.False(s.Pass);
        Assert.True(s.Errors.Keys.Any(k => k.StartsWith("unpair:")));
    }

    // ==================== 假件 ====================

    private static BleDiscoveryResult Discovery(int services) => new()
    {
        Ok = true,
        Services = Enumerable.Range(0, services).Select(i => new BleGattServiceInfo
        {
            Uuid = $"0000{i:X4}-0000-1000-8000-00805F9B34FB",
            Handle = (ushort)(i * 10)
        }).ToList()
    };

    private sealed class FakeBleCentral : IBleCentral
    {
        private readonly BleDiscoveryResult _discovery;
        private readonly int _mtu;

        public FakeBleCentral(BleDiscoveryResult discovery, int mtu = 23)
        {
            _discovery = discovery;
            _mtu = mtu;
        }

        public Queue<BleConnectResult> ConnectResults { get; } = new();
        public BlePairResult NextPair { get; set; } = new() { Ok = true, StatusText = "Paired" };
        public BlePairResult NextUnpair { get; set; } = new() { Ok = true, StatusText = "Unpaired" };
        public int ConnectCalls { get; private set; }
        public int DiscoverCalls { get; private set; }
        public int DisconnectCalls { get; private set; }
        public int PairCalls { get; private set; }
        public int UnpairCalls { get; private set; }

        public bool IsConnected { get; private set; }
        public string? ConnectedMacDisplay => IsConnected ? "AA:BB:CC:DD:EE:FF" : null;
#pragma warning disable CS0067 // 假件不触发连接事件
        public event EventHandler<bool>? ConnectionChanged;
#pragma warning restore CS0067

        public Task<BleConnectResult> ConnectAsync(BleConnectRequest request, CancellationToken ct = default)
        {
            ConnectCalls++;
            IsConnected = ConnectResults.Count == 0;   // 预置失败队列外的都成功
            return Task.FromResult(ConnectResults.Count > 0
                ? ConnectResults.Dequeue()
                : new BleConnectResult { Ok = true, MacDisplay = request.MacDisplay, Mtu = _mtu, ConnectMs = 42 });
        }

        public Task DisconnectAsync()
        {
            DisconnectCalls++;
            IsConnected = false;
            return Task.CompletedTask;
        }

        public Task<BleDiscoveryResult> DiscoverAsync(bool uncached = true, CancellationToken ct = default)
        {
            DiscoverCalls++;
            return Task.FromResult(_discovery);
        }

        public Task<BleValueResult> ReadAsync(string uuid, CancellationToken ct = default)
            => Task.FromResult(new BleValueResult { Ok = true, Data = new byte[] { 0x55 } });

        public Task<BleOpResult> WriteAsync(string uuid, byte[] data, bool withoutResponse = false,
            CancellationToken ct = default)
            => Task.FromResult(new BleOpResult { Ok = true });

        public Task<BleOpResult> SubscribeAsync(string uuid, TimeSpan duration, Action<string>? onHex,
            CancellationToken ct = default)
            => Task.FromResult(new BleOpResult { Ok = true });

        public Task<BlePairResult> PairAsync(string protection = "encryption", CancellationToken ct = default)
        {
            PairCalls++;
            return Task.FromResult(NextPair);
        }

        public Task<BlePairResult> UnpairAsync(CancellationToken ct = default)
        {
            UnpairCalls++;
            return Task.FromResult(NextUnpair);
        }
    }
}
