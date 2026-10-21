// <copyright file="Program.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Communication;
using PencilBox.Core.OtaTool;

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
    private static readonly string[] ValueOptions = { "--serial", "--sver", "--chunk-gap-ms", "--abort-after-block", "--log", "--fw" };

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
                "identify" or "id" => IdentifyAsync(GetOption(rest, "--serial")).GetAwaiter().GetResult(),
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
        for (var i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            var anchor = g.CommandChannel;
            Out.Info($"  [{i + 1}] SN {Show(anchor.SerialNumber)}    位置 {anchor.LocationToken}    通道 {g.Channels.Count} 个");
            foreach (var ch in g.Channels)
                Out.Info($"        · {ch.InterfaceLabel}");
        }
        return 0;
    }

    // ================= identify =================

    private static async Task<int> IdentifyAsync(string? serial)
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
        Out.Info($"读取版本 ← {device.InterfaceLabel}（SN {Show(device.SerialNumber)}）");
        var (info, err) = await Engine.ReadVersionAsync(device, Cts.Token);
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

        Out.Info($"固件 {fw.FileName}：{fw.Image.Length}B（{(fw.Image.Length + 1023) / 1024} 块）CRC16=0x{fw.Crc16:X4} 目标版本 {fw.SverText}");
        if (fw.Warning != null)
            Out.Warn(fw.Warning);

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

        BeginLog(fw, queue);
        Out.Warn(queue.Count > 1
            ? $"共 {queue.Count} 台进入逐支升级队列——升级全程请勿拔下任何设备"
            : "升级全程请勿拔下设备");

        var results = new List<OtaDeviceResult>();
        foreach (var (device, index) in queue.Select((d, i) => (d, i)))
        {
            if (Cts.IsCancellationRequested)
                break;
            Out.Info($"—— [{index + 1}/{queue.Count}] SN {Show(device.SerialNumber)}（位置 {device.LocationToken}）——");
            _lastPercent = -100;
            var result = await Engine.UpgradeOneAsync(device, fw, abortAfterBlock, chunkGapMs, noRecheck, Cts.Token);
            results.Add(result);
            Log(result.SummaryLine);
            Out.Info(result.SummaryLine);
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

    // ================= 输出/日志/杂项 =================

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

    private static void BeginLog(OtaFirmwarePackage fw, List<HidDeviceInfo> queue)
    {
        _logLines = new List<string>();
        Log($"PencilBox 裁剪版升级小工具（CLI）  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Log($"机器：{Environment.MachineName}    固件包：{fw.FileName}    目标版本：{fw.SverText}");
        Log($"队列 {queue.Count} 台：{string.Join("、", queue.Select(d => Show(d.SerialNumber) + "/位置" + d.LocationToken))}");
    }

    private static void Log(string line) => _logLines.Add($"{DateTime.Now:HH:mm:ss.fff} {line}");

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
            PencilBox 裁剪版升级小工具（基站/dongle USB 升级，多支同插按设备分组区分）

            用法：
              ota-tool scan                                  列出在插的全部可升级设备（按设备体系分组，一台一行）
              ota-tool identify [--serial SN|位置]           读基站版本（0x03；一台自动，多台需 --serial/位置）
              ota-tool ota <fw.bin> [选项]                   升级（多台逐支队列）

            ota 选项：
              --serial SN|位置       选台：序列号（支持唯一前缀）；多台同号时用路径位置片段（如 "9&168367ac&0"，须加引号）
              --all                  逐支升级全部在插设备（失败单支标记，不阻塞其余）
              --sver M.m.p           目标版本（缺省从包名解析：R1D_1.0.2_... → 1.0.2）
              --chunk-gap-ms N       片间隔（缺省 5ms，冻结值勿改）
              --abort-after-block N  发完 N 块主动退出（验证用：设备留旧固件可重试）
              --no-recheck           升级后不等设备回来复读版本（缺省等：等路径消失→等重现 ≤120s→宽限对拍）
              --log <path>           日志文件（缺省 exe 旁 ota-log_时间戳.txt）

            示例：
              ota-tool scan
              ota-tool ota R1D_1.0.2_rc.1_win_4f245b6.bin --all
              ota-tool ota R1D_1.0.2_rc.1_win_4f245b6.bin --serial CD71
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
