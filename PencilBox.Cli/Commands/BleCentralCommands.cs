using PencilBox.Cli.Output;
using PencilBox.Core.Ble;
using PencilBox.Core.Communication;

namespace PencilBox.Cli.Commands;

/// <summary>
/// BLE 中心设备命令面（PC"类基站"测试位）——对笔端/键盘做扫描→建连→GATT 枚举→读写订阅→SMP 配对→反复重连的
/// 整周期验证，专测 GAP/SMP 稳定性。定位是**通用测试通道**（标准蓝牙协议面，不绑定具体产品），
/// 不替代基站业务功能。
/// 命令间无跨进程连接状态：每条命令自建自拆（connect → 动作 → disconnect），与 company 域的每命令一 FSM 同风格。
/// 手册：_share/issues/0-待处理/命令行操作与OTA技能化/03（R1~R6 测试配方与判据）。
/// </summary>
public class BleCentralCommands
{
    private readonly IBleCentral _central;
    private readonly IBleScanner _scanner;

    public BleCentralCommands(IBleCentral central, IBleScanner scanner)
    {
        _central = central;
        _scanner = scanner;
    }

    /// <summary>`ble connect`：建连探针（GAP）——连上报告名字/MTU/耗时，按 `--hold` 保持后断开。</summary>
    public async Task<int> ConnectAsync(string? mac, string? name, bool forcePublic, bool forceRandom,
        int timeoutMs, int holdSec, bool maintain)
    {
        var (macDisplay, addrType) = await ResolveTargetAsync(mac, name, forcePublic, forceRandom);
        if (macDisplay == null)
            return 1;

        ConsoleFormatter.Info($"[BLE] 建连 {macDisplay}（地址类型 {addrType}，超时 {timeoutMs}ms" +
                              $"{(maintain ? "，维持连接" : "")}）…");
        var r = await _central.ConnectAsync(new BleConnectRequest
        {
            MacDisplay = macDisplay,
            AddressType = addrType,
            TimeoutMs = timeoutMs,
            MaintainConnection = maintain
        });

        if (!r.Ok)
        {
            ConsoleFormatter.Error($"建连失败：{r.Error}");
            return 1;
        }

        ConsoleFormatter.Success($"已连接：{(string.IsNullOrEmpty(r.Name) ? "(无名)" : r.Name)}" +
                                 $"  MTU={r.Mtu}（0=未报告）  耗时 {r.ConnectMs}ms");

        if (holdSec > 0)
        {
            ConsoleFormatter.Info($"保持 {holdSec}s（Ctrl+C 提前断开）…");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(holdSec));
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            try { await Task.Delay(Timeout.Infinite, cts.Token); }
            catch (OperationCanceledException) { }
        }

        await _central.DisconnectAsync();
        ConsoleFormatter.Info("已断开");
        return 0;
    }

    /// <summary>`ble discover`：GATT 全表枚举——PC 侧"客户端实搜"，对应基站 V1 对帐。</summary>
    public async Task<int> DiscoverAsync(string? mac, string? name, bool forcePublic, bool forceRandom,
        int timeoutMs, bool cached)
    {
        var (macDisplay, addrType) = await ResolveTargetAsync(mac, name, forcePublic, forceRandom);
        if (macDisplay == null || !await ConnectQuietAsync(macDisplay, addrType, timeoutMs))
            return 1;

        try
        {
            var r = await _central.DiscoverAsync(uncached: !cached);
            if (!r.Ok)
            {
                ConsoleFormatter.Error($"枚举失败：{r.Error}");
                return 1;
            }

            foreach (var svc in r.Services)
            {
                ConsoleFormatter.Info(
                    $"[SVC ] h=0x{svc.Handle:X4}  {svc.Uuid}  （{svc.Characteristics.Count} 特征）");
                foreach (var chr in svc.Characteristics)
                {
                    ConsoleFormatter.Info(
                        $"  [CHR] h=0x{chr.Handle:X4}  {BleUuid.ToDisplay(chr.Uuid),-38}  {chr.PropertiesText}");
                }
            }

            ConsoleFormatter.Success(
                $"枚举完成：{r.Services.Count} 服务 / {r.CharacteristicCount} 特征，耗时 {r.ElapsedMs}ms" +
                (cached ? "（系统缓存；改固件后请加 --uncached 直读设备）" : "（Uncached 直读设备）"));

            ConsoleFormatter.PrintObject(new
            {
                Services = r.Services.Count,
                Characteristics = r.CharacteristicCount,
                ElapsedMs = r.ElapsedMs,
                Mode = cached ? "cached" : "uncached"
            });
            return 0;
        }
        finally
        {
            await _central.DisconnectAsync();
        }
    }

    /// <summary>`ble read`：读特征值（HEX + ASCII 预览）。</summary>
    public async Task<int> ReadAsync(string? mac, string? name, string uuid, int timeoutMs)
    {
        var (macDisplay, addrType) = await ResolveTargetAsync(mac, name, false, false);
        if (macDisplay == null || !await ConnectQuietAsync(macDisplay, addrType, timeoutMs))
            return 1;

        try
        {
            var r = await _central.ReadAsync(uuid);
            if (!r.Ok)
            {
                ConsoleFormatter.Error(r.Error);
                return 1;
            }
            ConsoleFormatter.Success($"READ {BleUuid.ToDisplay(BleUuid.Normalize(uuid))}：{r.HexText}（{r.Data.Length}B）");
            if (r.Data.Length > 0 && r.Data.All(b => b >= 0x20 && b < 0x7F))
                ConsoleFormatter.Info($"  ASCII: \"{r.AsciiPreview}\"");
            return 0;
        }
        catch (ArgumentException ex)
        {
            ConsoleFormatter.Error(ex.Message);
            return 1;
        }
        finally
        {
            await _central.DisconnectAsync();
        }
    }

    /// <summary>`ble write`：写特征值。</summary>
    public async Task<int> WriteAsync(string? mac, string? name, string uuid, string hex, bool noResponse, int timeoutMs)
    {
        byte[] data;
        try
        {
            data = Convert.FromHexString(hex.Replace(" ", "").Replace("-", ""));
        }
        catch (FormatException)
        {
            ConsoleFormatter.Error($"HEX 解析失败：{hex}");
            return 1;
        }

        var (macDisplay, addrType) = await ResolveTargetAsync(mac, name, false, false);
        if (macDisplay == null || !await ConnectQuietAsync(macDisplay, addrType, timeoutMs))
            return 1;

        try
        {
            var r = await _central.WriteAsync(uuid, data, noResponse);
            if (!r.Ok)
            {
                ConsoleFormatter.Error(r.Error);
                return 1;
            }
            ConsoleFormatter.Success($"WRITE {BleUuid.ToDisplay(BleUuid.Normalize(uuid))} ← {Convert.ToHexString(data)}" +
                                     $"（{data.Length}B，{(noResponse ? "无响应" : "带响应")}）");
            return 0;
        }
        catch (ArgumentException ex)
        {
            ConsoleFormatter.Error(ex.Message);
            return 1;
        }
        finally
        {
            await _central.DisconnectAsync();
        }
    }

    /// <summary>`ble notify`：订阅通知收流（CCCD），实时打印，结束后退订并汇总。</summary>
    public async Task<int> NotifyAsync(string? mac, string? name, string uuid, int durationSec, int timeoutMs)
    {
        var (macDisplay, addrType) = await ResolveTargetAsync(mac, name, false, false);
        if (macDisplay == null || !await ConnectQuietAsync(macDisplay, addrType, timeoutMs))
            return 1;

        try
        {
            ConsoleFormatter.Info($"[BLE] 订阅 {BleUuid.ToDisplay(BleUuid.Normalize(uuid))}，收流 {durationSec}s（Ctrl+C 提前结束）…");
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, durationSec)));
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopCts.Cancel(); };

            var r = await _central.SubscribeAsync(uuid, TimeSpan.FromSeconds(Math.Max(1, durationSec)),
                hex =>
                {
                    var ts = DateTime.Now.ToString("HH:mm:ss.fff");
                    Console.WriteLine($"[{ts}] NTF {hex.Length / 2,3}B: {hex}");
                }, stopCts.Token);

            if (!r.Ok)
            {
                ConsoleFormatter.Error(r.Error);
                return 1;
            }
            ConsoleFormatter.Success($"收流结束：共 {r.Notifications} 条通知");
            return 0;
        }
        catch (ArgumentException ex)
        {
            ConsoleFormatter.Error(ex.Message);
            return 1;
        }
        finally
        {
            await _central.DisconnectAsync();
        }
    }

    /// <summary>`ble pair`：触发 SMP 配对/绑定（保护级别 default/none/encryption/authentication）。</summary>
    public async Task<int> PairAsync(string? mac, string? name, string protection, int timeoutMs)
    {
        var (macDisplay, addrType) = await ResolveTargetAsync(mac, name, false, false);
        if (macDisplay == null || !await ConnectQuietAsync(macDisplay, addrType, timeoutMs))
            return 1;

        try
        {
            if (protection is "authentication")
                ConsoleFormatter.Info("[BLE] MITM 级配对：如系统弹出确认/输入 PIN 窗口，请在窗口完成交互（默认 PIN 000000 仅作占位）。");
            var r = await _central.PairAsync(protection);
            if (!r.Ok)
            {
                ConsoleFormatter.Error($"配对失败：{r.StatusText ?? r.Error}");
                return 1;
            }
            ConsoleFormatter.Success($"配对完成：{r.StatusText}");
            return 0;
        }
        finally
        {
            await _central.DisconnectAsync();
        }
    }

    /// <summary>`ble unpair`：解除配对/绑定（清 bond）。</summary>
    public async Task<int> UnpairAsync(string? mac, string? name, int timeoutMs)
    {
        var (macDisplay, addrType) = await ResolveTargetAsync(mac, name, false, false);
        if (macDisplay == null || !await ConnectQuietAsync(macDisplay, addrType, timeoutMs))
            return 1;

        try
        {
            var r = await _central.UnpairAsync();
            if (!r.Ok)
            {
                ConsoleFormatter.Error($"退配失败：{r.StatusText ?? r.Error}");
                return 1;
            }
            ConsoleFormatter.Success($"退配完成：{r.StatusText}");
            return 0;
        }
        finally
        {
            await _central.DisconnectAsync();
        }
    }

    /// <summary>
    /// `ble watch`：连接状态观测（断连-回连时间线）——用平台 MaintainConnection 保持链路，
    /// 记录每次 Disconnected/Connected 事件与间隔，验证对端断连后恢复广播/回连行为（GAP 稳定性）。
    /// </summary>
    public async Task<int> WatchAsync(string? mac, string? name, int durationSec, int timeoutMs)
    {
        var (macDisplay, addrType) = await ResolveTargetAsync(mac, name, false, false);
        if (macDisplay == null || !await ConnectQuietAsync(macDisplay, addrType, timeoutMs, maintain: true))
            return 1;

        int connects = 1, disconnects = 0;
        var lastEvent = DateTime.Now;
        var timeline = new List<string>();

        void OnChanged(object? s, bool connected)
        {
            var now = DateTime.Now;
            var gap = (now - lastEvent).TotalSeconds;
            lastEvent = now;
            if (connected)
            {
                connects++;
                Console.WriteLine($"[{now:HH:mm:ss.fff}] CONNECTED（断开后 {gap:F1}s 回连）");
            }
            else
            {
                disconnects++;
                Console.WriteLine($"[{now:HH:mm:ss.fff}] DISCONNECTED（连接保持 {gap:F1}s）");
            }
            timeline.Add($"{(connected ? "C" : "D")}");
        }

        _central.ConnectionChanged += OnChanged;
        ConsoleFormatter.Info($"[BLE] 观测 {durationSec}s：断连/回连事件时间线（MaintainConnection=on，Ctrl+C 提前结束）…");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, durationSec)));
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            try { await Task.Delay(Timeout.Infinite, cts.Token); }
            catch (OperationCanceledException) { }
        }
        finally
        {
            _central.ConnectionChanged -= OnChanged;
            await _central.DisconnectAsync();
        }

        ConsoleFormatter.Success(
            $"观测结束：连接 {connects} 次 / 断开 {disconnects} 次；事件序列 {string.Join(" ", timeline)}");
        return 0;
    }

    /// <summary>`ble stress`：反复重连/配对压测（GAP/SMP 稳定性主入口），汇总 PASS/FAIL。</summary>
    public async Task<int> StressAsync(string? mac, string? name, int rounds, int holdSec, int gapSec,
        bool noDiscover, bool pairCycle, int allowFailures, int timeoutMs, string protection)
    {
        var (macDisplay, addrType) = await ResolveTargetAsync(mac, name, false, false);
        if (macDisplay == null)
            return 1;

        var options = new BleStressOptions
        {
            Rounds = rounds,
            HoldSec = holdSec,
            GapSec = gapSec,
            Discover = !noDiscover,
            PairCycle = pairCycle,
            AllowFailures = allowFailures,
            ConnectTimeoutMs = timeoutMs,
            PairProtection = protection
        };
        var request = new BleConnectRequest { MacDisplay = macDisplay, AddressType = addrType };

        ConsoleFormatter.Info($"[BLE] 压测：{rounds} 回合 ×（建连{(options.Discover ? "+枚举" : "")}" +
                              $"{(pairCycle ? "+配对/退配" : "")}{(holdSec > 0 ? $"+保持{holdSec}s" : "")}→断连，间隔 {gapSec}s）");

        var summary = await BleStressRunner.RunAsync(_central, request, options,
            (done, total) => ConsoleFormatter.Info($"  —— 回合 {done}/{total} 完成 ——"));

        ConsoleFormatter.PrintObject(new
        {
            summary.TotalRounds,
            summary.PassedRounds,
            summary.FailedRounds,
            summary.AllowedFailures,
            summary.Pass,
            ConnectAvgMs = summary.AvgConnectMs,
            ConnectMaxMs = summary.MaxConnectMs,
            summary.MinMtu,
            summary.Errors
        });

        if (summary.Pass)
        {
            ConsoleFormatter.Success($"[STRESS] PASS（{summary.PassedRounds}/{summary.TotalRounds} 回合成功" +
                                     $",均耗 {summary.AvgConnectMs ?? 0}ms，峰值 {summary.MaxConnectMs}ms）");
            return 0;
        }

        ConsoleFormatter.Error($"[STRESS] FAIL（成功 {summary.PassedRounds}/{summary.TotalRounds}，" +
                               $"失败 {summary.FailedRounds} > 允许 {summary.AllowedFailures}）" +
                               (summary.Errors.Count > 0
                                   ? " 首要原因：" + string.Join("；", summary.Errors.Select(kv => $"{kv.Key}×{kv.Value}"))
                                   : ""));
        return 1;
    }

    // ==================== 内部 ====================

    /// <summary>
    /// 解析目标：`--mac` 直接用（`--public/--random` 强制地址类型）；否则按 `--name` 子串扫描匹配
    /// （取 RSSI 最强者），再不行用 recommend。地址类型优先取扫描所得（省试探）。
    /// </summary>
    private async Task<(string? Mac, BleAddressType Type)> ResolveTargetAsync(
        string? mac, string? name, bool forcePublic, bool forceRandom)
    {
        var type = forcePublic ? BleAddressType.Public
                 : forceRandom ? BleAddressType.Random
                 : BleAddressType.Unknown;

        if (!string.IsNullOrWhiteSpace(mac))
            return (FormatMac(mac), type);

        ConsoleFormatter.Info("[BLE] 未给 --mac：扫描 5s 按 " +
                              (string.IsNullOrWhiteSpace(name) ? "SunVote 笔端识别" : $"名称含 \"{name}\"") + " 选目标…");
        var devices = await _scanner.ScanAsync(5);
        var hit = string.IsNullOrWhiteSpace(name)
            ? BleNaming.Recommend(devices)
            : devices.Where(d => (d.LocalName ?? "").Contains(name, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(d => d.Rssi).FirstOrDefault();

        if (hit == null)
        {
            ConsoleFormatter.Error("扫描未找到目标（确认笔端在广播；或用 --mac 直接指定）");
            return (null, type);
        }

        ConsoleFormatter.Info($"[BLE] 目标：{hit.MacDisplayText} \"{hit.LocalName}\" rssi={hit.Rssi}dBm" +
                              $"（地址类型 {hit.AddressType}）");
        return (hit.MacDisplayText, type != BleAddressType.Unknown ? type : hit.AddressType);
    }

    private static string FormatMac(string mac)
    {
        try
        {
            var bytes = MacAddress.ParseDisplay(mac);
            return MacAddress.FormatDisplay(bytes);
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"MAC 解析失败：{ex.Message}");
        }
    }

    /// <summary>静默建连（错误直接报出）；maintain=true 时平台保持链路（watch 用）。</summary>
    private async Task<bool> ConnectQuietAsync(string macDisplay, BleAddressType type,
        int timeoutMs, bool maintain = false)
    {
        var r = await _central.ConnectAsync(new BleConnectRequest
        {
            MacDisplay = macDisplay,
            AddressType = type,
            TimeoutMs = timeoutMs,
            MaintainConnection = maintain
        });
        if (!r.Ok)
        {
            ConsoleFormatter.Error($"建连失败：{r.Error}");
            return false;
        }
        ConsoleFormatter.Info($"[BLE] 已连接 {macDisplay}" +
                              (string.IsNullOrEmpty(r.Name) ? "" : $" \"{r.Name}\"") +
                              $" MTU={r.Mtu} {r.ConnectMs}ms");
        return true;
    }
}
