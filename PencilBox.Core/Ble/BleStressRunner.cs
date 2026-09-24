using PencilBox.Core.Communication;

namespace PencilBox.Core.Ble;

/// <summary>反复重连/配对压测选项（GAP/SMP 稳定性验证）。</summary>
public sealed class BleStressOptions
{
    /// <summary>回合数（每回合 = 建连→[发现]→[配对/退配]→保持→断连→间隔）</summary>
    public int Rounds { get; init; } = 10;

    /// <summary>每回合建连后的保持秒数（0=连上即断）</summary>
    public int HoldSec { get; init; } = 0;

    /// <summary>回合间隔秒数（断连后等待对端恢复广播的时间）</summary>
    public int GapSec { get; init; } = 1;

    /// <summary>每回合是否做 GATT 全表枚举（顺带验证服务面稳定）</summary>
    public bool Discover { get; init; } = true;

    /// <summary>每回合是否做 配对→退配（SMP 反复配对压测；关掉则只压 GAP 连接）</summary>
    public bool PairCycle { get; init; }

    /// <summary>判定 PASS 允许的最大失败回合数</summary>
    public int AllowFailures { get; init; }

    /// <summary>单次建连超时（毫秒）</summary>
    public int ConnectTimeoutMs { get; init; } = 10_000;

    /// <summary>配对保护级别（透传给 <see cref="IBleCentral.PairAsync"/>）</summary>
    public string PairProtection { get; init; } = "encryption";
}

/// <summary>单回合记录。</summary>
public sealed class BleRoundRecord
{
    public int Round { get; init; }
    public bool ConnectOk { get; init; }
    public int ConnectMs { get; init; }
    /// <summary>null=本回合未做枚举</summary>
    public bool? DiscoverOk { get; init; }
    public int ServiceCount { get; init; }
    public int Mtu { get; init; }
    /// <summary>null=本回合未做配对环</summary>
    public bool? PairOk { get; init; }
    public bool? UnpairOk { get; init; }
    /// <summary>首个失败原因（成功为 null）</summary>
    public string? Error { get; init; }

    public bool Ok => ConnectOk && DiscoverOk is not false && PairOk is not false && UnpairOk is not false;
}

/// <summary>压测汇总（PASS/FAIL 判定 + 延迟画像）。</summary>
public sealed class BleStressSummary
{
    public int TotalRounds { get; init; }
    public int PassedRounds { get; init; }
    public int FailedRounds { get; init; }
    public int AllowedFailures { get; init; }
    /// <summary>最终判定：失败回合数 ≤ 允许值 且 至少 1 回合成功</summary>
    public bool Pass { get; init; }
    /// <summary>成功回合的建连均耗时（毫秒；无成功回合为 null）</summary>
    public double? AvgConnectMs { get; init; }
    public int MaxConnectMs { get; init; }
    /// <summary>观测到的最小 MTU（0=未观测到）</summary>
    public int MinMtu { get; init; }
    /// <summary>失败原因 → 次数（汇总排障用）</summary>
    public IReadOnlyDictionary<string, int> Errors { get; init; } =
        new Dictionary<string, int>();

    public static BleStressSummary Compute(IReadOnlyList<BleRoundRecord> records, int allowedFailures)
    {
        var failed = records.Where(r => !r.Ok).ToList();
        var okConnect = records.Where(r => r.ConnectOk).ToList();
        var errors = failed.Where(r => r.Error != null)
            .GroupBy(r => r.Error!)
            .ToDictionary(g => g.Key, g => g.Count());

        return new BleStressSummary
        {
            TotalRounds = records.Count,
            PassedRounds = records.Count - failed.Count,
            FailedRounds = failed.Count,
            AllowedFailures = allowedFailures,
            Pass = failed.Count <= allowedFailures && okConnect.Count > 0,
            AvgConnectMs = okConnect.Count > 0
                ? Math.Round(okConnect.Average(r => (double)r.ConnectMs), 1)
                : null,
            MaxConnectMs = okConnect.Count > 0 ? okConnect.Max(r => r.ConnectMs) : 0,
            MinMtu = okConnect.Select(r => r.Mtu).Where(m => m > 0).DefaultIfEmpty(0).Min(),
            Errors = errors
        };
    }
}

/// <summary>
/// 反复重连/配对压测编排（纯逻辑：只依赖 <see cref="IBleCentral"/>，可用假件单测）。
/// 每回合 = 建连 →（可选）全表枚举 →（可选）配对/退配 → 保持 → 断连 → 间隔；
/// 目的：用 PC 直接压 GAP（反复建连/断连/回连）与 SMP（反复配对/退配）稳定性，
/// 替代"必须靠基站抓日志"的测试方式。
/// </summary>
public static class BleStressRunner
{
    public static async Task<BleStressSummary> RunAsync(
        IBleCentral central, BleConnectRequest connect, BleStressOptions options,
        Action<int, int>? onRoundDone = null, CancellationToken ct = default)
    {
        var records = new List<BleRoundRecord>();

        for (int round = 1; round <= options.Rounds; round++)
        {
            ct.ThrowIfCancellationRequested();
            records.Add(await RunRoundAsync(central, connect, options, round, ct));
            onRoundDone?.Invoke(round, options.Rounds);

            if (round < options.Rounds && options.GapSec > 0)
                await Task.Delay(TimeSpan.FromSeconds(options.GapSec), ct);
        }

        return BleStressSummary.Compute(records, options.AllowFailures);
    }

    private static async Task<BleRoundRecord> RunRoundAsync(
        IBleCentral central, BleConnectRequest connect, BleStressOptions options,
        int round, CancellationToken ct)
    {
        int mtu = 0;
        bool? discoverOk = null;
        int services = 0;
        bool? pairOk = null;
        bool? unpairOk = null;
        string? error = null;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var conn = await central.ConnectAsync(connect with { TimeoutMs = options.ConnectTimeoutMs }, ct);
        sw.Stop();
        var connectMs = (int)sw.ElapsedMilliseconds;
        mtu = conn.Mtu;

        if (!conn.Ok)
        {
            return new BleRoundRecord
            {
                Round = round, ConnectOk = false, ConnectMs = connectMs,
                DiscoverOk = options.Discover, PairOk = options.PairCycle,
                Error = $"connect: {conn.Error}"
            };
        }

        try
        {
            if (options.Discover)
            {
                var disc = await central.DiscoverAsync(uncached: true, ct);
                discoverOk = disc.Ok;
                services = disc.Services.Count;
                if (!disc.Ok)
                    error = $"discover: {disc.Error}";
            }

            if (error == null && options.PairCycle)
            {
                var pair = await central.PairAsync(options.PairProtection, ct);
                pairOk = pair.Ok;
                if (!pair.Ok) error = $"pair: {pair.StatusText ?? pair.Error}";

                if (pair.Ok)
                {
                    var unpair = await central.UnpairAsync(ct);
                    unpairOk = unpair.Ok;
                    if (!unpair.Ok) error = $"unpair: {unpair.StatusText ?? unpair.Error}";
                }
            }

            if (error == null && options.HoldSec > 0)
                await Task.Delay(TimeSpan.FromSeconds(options.HoldSec), ct);
        }
        finally
        {
            await central.DisconnectAsync();
        }

        return new BleRoundRecord
        {
            Round = round, ConnectOk = true, ConnectMs = connectMs,
            DiscoverOk = discoverOk, ServiceCount = services, Mtu = mtu,
            PairOk = pairOk, UnpairOk = unpairOk, Error = error
        };
    }
}
