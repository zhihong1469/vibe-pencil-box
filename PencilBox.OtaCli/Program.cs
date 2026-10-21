// <copyright file="Program.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Communication;
using PencilBox.Core.OtaTool;
using PencilBox.Core.Protocol.Company;

namespace PencilBox.OtaCli;

/// <summary>
/// 外发裁剪小工具 · CLI（01/02/03 号）：
///   scan / identify / ota——多支 dongle 同插按设备体系分组（一行=一台，禁静默取第一台），
///   `--all` 逐支顺序队列（失败单支标记不阻塞），全程中文、日志落盘回传用。
/// 业务全在 <see cref="OtaToolEngine"/>（与 GUI 共用零漂移），本文件只是控制台壳。
/// 通道协议 = 基站自升级轨（USB HID itf3/EP4，F5 `0x16` 拉模式），与主线 CLI 同源零漂移。
/// </summary>
internal static class Program
{
    /// <summary>带值选项（位置参数解析时须连同其值一起跳过）。</summary>
    private static readonly string[] ValueOptions = { "--serial", "--sver", "--chunk-gap-ms", "--abort-after-block", "--log", "--fw", "--parallel", "--target" };

    /// <summary>控制台走内联回调（引擎事件在哪个线程发生就同步打印——与旧 CLI 行为一致）。</summary>
    private static readonly OtaToolEngine Engine = new(new InlineProgress(OnUpdate));

    private static List<string> _logLines = new();
    private static string? _logPath;
    private static readonly CancellationTokenSource Cts = new();
    private static int _lastPercent = -100;

    private static int Main(string[] args)
    {
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Cts.Cancel();   // 优雅收敛：FSM 发 DCMD=00 退出，日志照常落盘
        };

        if (args.Length == 0)
        {
            // 双击启动（无参）不闪退：用法 + 停住等回车
            Usage();
            Console.WriteLine();
            Out.Info("（命令行工具：无参数双击只显示本用法；升级请看上方示例）——按回车键退出…");
            Console.ReadLine();
            return 0;
        }

        var action = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();
        try
        {
            return action switch
            {
                "scan" or "list" => ScanAsync().GetAwaiter().GetResult(),
                "identify" or "id" => IdentifyAsync(GetOption(rest, "--serial"), rest.Contains("--pen")).GetAwaiter().GetResult(),
                "ota" or "upgrade" => OtaAsync(rest).GetAwaiter().GetResult(),
                "help" or "--help" or "-h" => Usage(),
                _ => UnknownCommand(action)
            };
        }
        finally
        {
            FlushLog();
        }
    }

    // ================= scan =================

    private static async Task<int> ScanAsync()
    {
        var groups = await Engine.ScanGroupsAsync();
        Out.Info($"在插可升级设备（命令通道）：{groups.Count} 台（按设备体系分组，一台一行）");

        // 每台并发读当前版本（GUI 刷新同款体验；4 路窗口防多台拖慢扫描）
        var versions = new string?[groups.Count];
        using var gate = new SemaphoreSlim(4);
        var readers = groups.Select(async (g, i) =>
        {
            try
            {
                await gate.WaitAsync(Cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                var (info, _) = await new OtaToolEngine().ReadVersionAsync(g.CommandChannel, OtaUpgradeTarget.BaseStation, Cts.Token);
                versions[i] = info == null ? null : $"{info.SverMajor}.{info.SverMinor}.{info.SverPatch}";
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();
        await Task.WhenAll(readers);

        for (var i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            var anchor = g.CommandChannel;
            Out.Info($"  [{i + 1}] SN {Show(anchor.SerialNumber)}    位置 {anchor.LocationToken}    当前版本 {versions[i] ?? "读取失败"}    通道 {g.Channels.Count} 个");
            foreach (var ch in g.Channels)
                Out.Info($"        · {ch.InterfaceLabel}");
        }
        return 0;
    }

    // ================= identify =================

    private static async Task<int> IdentifyAsync(string? serial, bool pen = false)
    {
        var candidates = await Engine.EnumerateCandidatesAsync();
        var selection = HidDeviceSelection.Select(candidates, serial);
        if (selection.Error != null || selection.Selected == null)
        {
            Out.Error(selection.Error ?? "设备选择失败");
            PrintCandidates(selection.Candidates);
            return selection.Candidates.Count > 1 && string.IsNullOrWhiteSpace(serial) ? 2 : 1;
        }

        var device = selection.Selected;
        var target = pen ? OtaUpgradeTarget.PenWireless : OtaUpgradeTarget.BaseStation;
        Out.Info($"读取版本（{OtaUpgradeTargetText.For(target)}）← {device.InterfaceLabel}（SN {Show(device.SerialNumber)}）");
        var (info, err) = await Engine.ReadVersionAsync(device, target, Cts.Token);
        if (info == null)
        {
            Out.Error(err ?? "0x03 无应答");
            return 1;
        }

        Log($"identify SN={device.SerialNumber} MODEL=0x{info.Model:X2} SVER={info.SverMajor}.{info.SverMinor}.{info.SverPatch}");
        Out.Ok($"SN {Show(device.SerialNumber)}  MODEL=0x{info.Model:X2}  SVER={info.SverMajor}.{info.SverMinor}.{info.SverPatch}");
        return 0;
    }

    // ================= ota =================

    private static async Task<int> OtaAsync(string[] rest)
    {
        var fwPath = GetOption(rest, "--fw") ?? GetFirstPositional(rest);
        var serial = GetOption(rest, "--serial");
        var all = rest.Contains("--all");
        var sverOverride = GetOption(rest, "--sver");
        var chunkGapMs = GetOptionInt(rest, "--chunk-gap-ms", -1);
        var abortAfterBlock = GetOptionInt(rest, "--abort-after-block", -1);
        var noRecheck = rest.Contains("--no-recheck");
        var parallelRaw = GetOptionInt(rest, "--parallel", 1);
        var parallel = parallelRaw < 1 ? 1 : (parallelRaw > 8 ? 8 : parallelRaw);   // net48 无 Math.Clamp
        var targetRaw = (GetOption(rest, "--target") ?? "bs").ToLowerInvariant();
        if (targetRaw is not ("bs" or "base" or "pen" or "pencil"))
        {
            Out.Error($"--target 非法：{targetRaw}（bs=基站 / pen=键盘·经基站无线）");
            return 1;
        }
        var target = targetRaw is "pen" or "pencil" ? OtaUpgradeTarget.PenWireless : OtaUpgradeTarget.BaseStation;

        if (fwPath is null || !File.Exists(fwPath))
        {
            Out.Error($"固件包不存在：{fwPath ?? "(未指定)"}——用法：ota-tool ota <fw.bin> [--serial SN | --all]");
            return 1;
        }

        // 包校验/装载在引擎（外发口径：R1P_ 硬拒、尺寸上限，无跳过校验入口）
        OtaFirmwarePackage fw;
        try
        {
            fw = Engine.LoadFirmware(fwPath, sverOverride);
        }
        catch (OtaFirmwareLoadException ex)
        {
            Out.Error(ex.Message);
            return 1;
        }

        Out.Info($"固件 {fw.FileName}：{fw.Image.Length}B（{(fw.Image.Length + 1023) / 1024} 块）CRC16=0x{fw.Crc16:X4} 目标版本 {fw.SverText} · 升级对象 {OtaUpgradeTargetText.For(target)}");
        if (fw.Warning != null)
            Out.Warn(fw.Warning);
        // 包名×升级对象错配：注意级不拦截（防呆不防傻——设备侧指纹/容量判据才是真闸；主线同款 FirmwareNaming）
        if (FirmwareNaming.TargetMismatchNotice(fw.FileName, target == OtaUpgradeTarget.PenWireless ? Downtype.Pen : Downtype.BaseStation) is { } mismatch)
            Out.Warn(mismatch);

        // 设备集合：--all = 全部命令通道实例；否则 1 台自动 / 多台要求 --serial（禁静默取第一台）
        var candidates = await Engine.EnumerateCandidatesAsync();
        List<HidDeviceInfo> queue;
        if (all)
        {
            if (candidates.Count == 0)
            {
                Out.Error("未找到可升级设备——确认接收器已插入且此电脑能识别");
                return 1;
            }
            queue = candidates;
        }
        else
        {
            var selection = HidDeviceSelection.Select(candidates, serial);
            if (selection.Error != null || selection.Selected == null)
            {
                Out.Error(selection.Error ?? "设备选择失败");
                PrintCandidates(selection.Candidates);
                return 2;
            }
            queue = new List<HidDeviceInfo> { selection.Selected };
        }

        BeginLog(fw, queue, parallel, target);
        if (parallel > 1 && queue.Count > 1)
            Out.Warn($"共 {queue.Count} 台并行升级（并发 {Math.Min(parallel, queue.Count)}）——升级全程请勿拔下任何设备");
        else
            Out.Warn(queue.Count > 1
                ? $"共 {queue.Count} 台进入逐支升级队列——升级全程请勿拔下任何设备"
                : "升级全程请勿拔下设备");

        var results = new List<OtaDeviceResult>();
        if (parallel > 1 && queue.Count > 1)
        {
            results = await RunParallelAsync(queue, fw, target, parallel, abortAfterBlock, chunkGapMs, noRecheck);
        }
        else
        {
            foreach (var (device, index) in queue.Select((d, i) => (d, i)))
            {
                if (Cts.IsCancellationRequested)
                    break;
                Out.Info($"—— [{index + 1}/{queue.Count}] SN {Show(device.SerialNumber)}（位置 {device.LocationToken}）——");
                _lastPercent = -100;
                var result = await Engine.UpgradeOneAsync(device, fw, target, abortAfterBlock, chunkGapMs, noRecheck, Cts.Token);
                results.Add(result);
                Log(result.SummaryLine);
                Out.Info(result.SummaryLine);
            }
        }

        // 汇总（02 号 §4-6）
        var okCount = results.Count(r => r.Outcome is OtaDeviceOutcome.Success or OtaDeviceOutcome.Skip);
        var failCount = results.Count(r => r.Outcome is OtaDeviceOutcome.Fail or OtaDeviceOutcome.Unconfirmed);
        var summary = $"升级结束：{okCount} 台成功（含跳过），{failCount} 台失败/待确认";
        if (failCount == 0)
            Out.Ok(summary + "——请拔下设备插回一体机；若一体机仍不识别，等 1 分钟再重插（重枚举可慢）");
        else
            Out.Error(summary + $"——详情见日志：{EffectiveLogPath()}");
        Log(summary);
        return failCount == 0 ? 0 : 1;
    }

    /// <summary>
    /// 滑动窗口并行升级（03 号 §10）：每台独立引擎实例（通道/FSM/探测互不共享），
    /// 失败单支标记不阻塞其余；Ctrl+C 经由同一 Cts 优雅收敛所有在途会话。
    /// </summary>
    private static async Task<List<OtaDeviceResult>> RunParallelAsync(
        List<HidDeviceInfo> queue, OtaFirmwarePackage fw, OtaUpgradeTarget target, int parallel,
        int abortAfterBlock, int chunkGapMs, bool noRecheck)
    {
        var total = queue.Count;
        var lastPcts = new int[total];
        for (var i = 0; i < total; i++)   // net48 无 Array.Fill
            lastPcts[i] = -100;
        using var gate = new SemaphoreSlim(parallel);

        var tasks = queue.Select((device, index) => Task.Run(async () =>
        {
            var tag = $"[{index + 1}/{total}]";
            try
            {
                await gate.WaitAsync(Cts.Token);
            }
            catch (OperationCanceledException)
            {
                return new OtaDeviceResult(device.SerialNumber, OtaDeviceOutcome.Abort, null,
                    "未开始（用户取消）", TimeSpan.Zero);
            }
            try
            {
                if (Cts.IsCancellationRequested)
                    return new OtaDeviceResult(device.SerialNumber, OtaDeviceOutcome.Abort, null,
                        "未开始（用户取消）", TimeSpan.Zero);

                var engine = new OtaToolEngine(new InlineProgress(u => OnUpdateTagged(tag, lastPcts, index, u)));
                Out.Info($"{tag} —— SN {Show(device.SerialNumber)}（位置 {device.LocationToken}）开始升级");
                var result = await engine.UpgradeOneAsync(device, fw, target, abortAfterBlock, chunkGapMs, noRecheck, Cts.Token);
                Log(result.SummaryLine);
                return result;
            }
            finally
            {
                gate.Release();
            }
        })).ToArray();

        await Task.WhenAll(tasks);
        return tasks.Select(t => t.Result).ToList();
    }

    // ================= 输出/日志/杂项 =================

    /// <summary>并行模式输出：行首 [i/N] 台标（进度节流逐台独立；阶段行只进日志降噪）。</summary>
    private static void OnUpdateTagged(string tag, int[] lastPcts, int slot, OtaToolUpdate update)
    {
        switch (update)
        {
            case OtaToolUpdate.Log log:
                var msg = $"{tag} {log.Message}";
                switch (log.Level)
                {
                    case "ok": Out.Ok(msg); break;
                    case "warn": Out.Warn(msg); break;
                    case "error": Out.Error(msg); break;
                    default: Out.Info(msg); break;
                }
                Log(msg);
                break;
            case OtaToolUpdate.Percent pct:
                if (pct.Value / 10 != lastPcts[slot] / 10)
                {
                    lastPcts[slot] = pct.Value;
                    var m = $"{tag} 进度 {pct.Value,3}%（{pct.BlocksDone}/{pct.TotalBlocks} 块）";
                    Out.Info(m);
                    Log(m);
                }
                break;
            case OtaToolUpdate.Phase phase:
                Log($"{tag} [阶段] {phase.Name}");
                break;
        }
    }

    /// <summary>引擎更新 → 控制台 + 日志（内联，与旧 CLI 打印节奏一致）。</summary>
    private static void OnUpdate(OtaToolUpdate update)
    {
        switch (update)
        {
            case OtaToolUpdate.Log log:
                switch (log.Level)
                {
                    case "ok": Out.Ok(log.Message); break;
                    case "warn": Out.Warn(log.Message); break;
                    case "error": Out.Error(log.Message); break;
                    default: Out.Info(log.Message); break;
                }
                Log(log.Message);
                break;
            case OtaToolUpdate.Percent pct:
                if (pct.Value / 10 != _lastPercent / 10)
                {
                    _lastPercent = pct.Value;
                    Out.Info($"    进度 {pct.Value,3}%（{pct.BlocksDone}/{pct.TotalBlocks} 块）");
                    Log($"    进度 {pct.Value}%（{pct.BlocksDone}/{pct.TotalBlocks}）");
                }
                break;
            case OtaToolUpdate.Phase phase:
                Log($"    [阶段] {phase.Name}");
                break;
        }
    }

    /// <summary>控制台输出（中文；颜色区分级别——WARN 不等于失败，与主线口径一致）。</summary>
    private static class Out
    {
        public static void Info(string m) { Console.ForegroundColor = ConsoleColor.Gray; Console.WriteLine(m); Console.ResetColor(); }
        public static void Ok(string m) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine(m); Console.ResetColor(); }
        public static void Warn(string m) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine(m); Console.ResetColor(); }
        public static void Error(string m) { Console.ForegroundColor = ConsoleColor.Red; Console.Error.WriteLine(m); Console.ResetColor(); }
    }

    private static void PrintCandidates(IReadOnlyList<HidDeviceInfo> candidates)
    {
        for (var i = 0; i < candidates.Count; i++)
        {
            var d = candidates[i];
            Out.Info($"  [{i + 1}] SN {Show(d.SerialNumber)}    位置 {d.LocationToken}    {d.InterfaceLabel}    UsagePage 0x{d.UsagePage:X4}");
        }
    }

    private static string Show(string s) => string.IsNullOrEmpty(s) ? "(无序列号)" : s;

    private static void BeginLog(OtaFirmwarePackage fw, List<HidDeviceInfo> queue, int parallel, OtaUpgradeTarget target)
    {
        _logLines = new List<string>();
        Log($"PencilBox 裁剪版升级小工具（CLI）  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Log($"机器：{Environment.MachineName}    固件包：{fw.FileName}    目标版本：{fw.SverText}    升级对象：{OtaUpgradeTargetText.For(target)}    并发：{parallel}");
        Log($"队列 {queue.Count} 台：{string.Join("、", queue.Select(d => Show(d.SerialNumber) + "/位置" + d.LocationToken))}");
    }

    private static readonly object _logLock = new();

    private static void Log(string line)
    {
        lock (_logLock)
            _logLines.Add($"{DateTime.Now:HH:mm:ss.fff} {line}");
    }

    private static void FlushLog()
    {
        if (_logLines.Count == 0)
            return;
        try
        {
            var path = EffectiveLogPath();
            File.WriteAllLines(path, _logLines, new System.Text.UTF8Encoding(true));
            Out.Info($"日志已保存：{path}");
        }
        catch (Exception ex)
        {
            Out.Warn($"日志写盘失败：{ex.Message}");
        }
        _logLines = new List<string>();
    }

    /// <summary>日志路径：--log 优先；未给则定为 exe 旁 ota-log_时间戳.txt（解析即写回，汇总行与落盘同路径）。</summary>
    private static string EffectiveLogPath()
    {
        if (_logPath != null)
            return _logPath;
        var dir = Path.Combine(AppContext.BaseDirectory, "log");
        Directory.CreateDirectory(dir);
        return _logPath = Path.Combine(dir, $"ota-log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
    }

    private static string? GetOption(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int GetOptionInt(string[] args, string name, int fallback) =>
        int.TryParse(GetOption(args, name), out var v) ? v : fallback;

    /// <summary>首个位置参数：跳过选项自身及其值（ValueOptions 之后的那个 token 不是位置参数）。</summary>
    private static string? GetFirstPositional(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("-"))
            {
                if (ValueOptions.Contains(a))
                    i++;   // 连同值一起跳过
                continue;
            }
            return a;
        }
        return null;
    }

    private static int Usage()
    {
        Console.WriteLine("""
            PencilBox 裁剪版升级小工具（基站 USB 直升 + 键盘经基站无线中转，多支同插按设备分组）

            升级对象由 `--target` 显式选择（指挥官定版：不靠包名推断）：
              bs（缺省）=基站 USB 直升；pen=键盘，走同一 USB 通道经基站 BLE 无线中转（键盘须已与对应基站配对）。
            包名前缀与升级对象错配时仅提示不拦截（防呆不防傻）。

            用法：
              ota-tool scan                                  列出在插的全部可升级设备（按设备体系分组，一台一行，附当前版本）
              ota-tool identify [--pen] [--serial SN|位置]   读版本（0x03；缺省读基站，--pen 经基站透读键盘）
              ota-tool ota <fw.bin> [选项]                   升级（多台逐支队列；--parallel N 台并行）

            ota 选项：
              --target bs|pen        升级对象：bs=基站（缺省）/ pen=键盘（经基站无线中转）
              --serial SN|位置       选台：序列号（支持唯一前缀）；多台同号时用路径位置片段（如 "9&168367ac&0"，须加引号）
              --all                  逐支升级全部在插设备（失败单支标记，不阻塞其余）
              --parallel N           并行升级：N 台同时刷（滑动窗口 1~8，缺省 1=逐台；每台独立通道互不干扰）
              --sver M.m.p           目标版本（缺省从包名解析：R1D_1.0.2_... → 1.0.2）
              --chunk-gap-ms N       片间隔（缺省 5ms，冻结值勿改）
              --abort-after-block N  发完 N 块主动退出（验证用：设备留旧固件可重试）
              --no-recheck           升级后不等设备回来复读版本（缺省主动探测读版本对拍 ≤120s）
              --log <path>           日志文件（缺省 exe 旁 log\ 子目录）

            示例：
              ota-tool scan
              ota-tool ota R1D_1.0.2_rc.1_win_4f245b6.bin --all
              ota-tool ota R1D_1.0.2_rc.1_win_4f245b6.bin --all --parallel 4
              ota-tool ota R1D_1.0.2_rc.1_win_4f245b6.bin --serial CD71
              ota-tool ota R1P_1.0.1_dev_gcc_b718f8e.bin --all --target pen    （键盘固件，经各基站无线下发到已配对键盘）
            """);
        return 0;
    }

    private static int UnknownCommand(string action)
    {
        Out.Error($"未知命令：{action}（可用：scan / identify / ota / help）");
        return 1;
    }

    /// <summary>控制台专用的内联进度（GUI 用 Progress&lt;T&gt; 自动回 UI 线程，控制台无同步上下文直接内联）。</summary>
    private sealed class InlineProgress(Action<OtaToolUpdate> action) : IProgress<OtaToolUpdate>
    {
        public void Report(OtaToolUpdate update) => action(update);
    }
}
