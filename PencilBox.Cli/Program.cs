// <copyright file="Program.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using PencilBox.Cli.Commands;
using PencilBox.Cli.Output;
using PencilBox.Core.Models;
using PencilBox.Core.Services;
using PencilBox.Infrastructure.Audio;
using PencilBox.Infrastructure.DependencyInjection;

namespace PencilBox.Cli;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Parse global options: extract --verbose and --output <fmt> before command dispatch
        bool verbose = false;
        var filteredArgs = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--verbose":
                case "-v":
                    verbose = true;
                    break;
                case "--output":
                case "-o":
                    if (i + 1 < args.Length)
                    {
                        ConsoleFormatter.Format = args[++i] switch
                        {
                            "json" => OutputFormat.Json,
                            "quiet" => OutputFormat.Quiet,
                            _ => OutputFormat.Table
                        };
                    }
                    break;
                default:
                    filteredArgs.Add(args[i]);
                    break;
            }
        }

        args = filteredArgs.ToArray();

        if (args.Length == 0)
        {
            PrintHelp();
            return 0;
        }

        // Setup DI
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            var logConfig = new LoggerConfiguration()
                .MinimumLevel.Is(verbose ? LogEventLevel.Debug : LogEventLevel.Warning)
                .WriteTo.Console(
                    outputTemplate: "[{Level:u3}] {Message:lj}{NewLine}{Exception}");
            builder.AddSerilog(logConfig.CreateLogger(), dispose: true);
        });

        services.AddPencilBoxInfrastructure();

        // Register CLI command handlers
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger>(sp => sp.GetRequiredService<ILogger<LatencyProbeEngine>>());  // LatencyProbeEngine 主 ctor 吃非泛型 ILogger
        services.AddSingleton<LatencyProbeEngine>();
        services.AddSingleton<DeviceCommands>();
        services.AddSingleton<BleCommands>();
        services.AddSingleton<BleCentralCommands>();
        services.AddSingleton<AudioCommands>();
        services.AddSingleton<SerialCommands>();
        services.AddSingleton<MacroCommands>();
        services.AddSingleton<DspCommands>();
        services.AddSingleton<ConfigCommands>();
        services.AddSingleton<CompanyCommands>();
        services.AddSingleton<CompanyParamCommands>();
        services.AddSingleton<CompanyStreamCommands>();
        services.AddSingleton<KeymapCommands>();
        services.AddSingleton<WorkStateCommands>();
        services.AddSingleton<FeatureCommands>();
        services.AddSingleton<SoftTriggerCommands>();

        var sp = services.BuildServiceProvider();

        try
        {
            var area = args[0].ToLowerInvariant();

            return area switch
            {
                "device" or "dev" => await DispatchDeviceAsync(sp, args.Skip(1).ToArray()),
                "ble" => await DispatchBleAsync(sp, args.Skip(1).ToArray()),

                // 门禁（14 号）：可选功能被 feature off 后整域拒绝并指路；核心域（device/ble/company/keymap/workstate）不受管
                "audio" or "aud" => await GateAsync(sp, "audio", () => DispatchAudioAsync(sp, args.Skip(1).ToArray())),
                "serial" or "ser" => await GateAsync(sp, "seriallog", () => DispatchSerialAsync(sp, args.Skip(1).ToArray())),
                "macro" => await DispatchMacroAsync(sp, args.Skip(1).ToArray()),
                "dsp" => await DispatchDspAsync(sp, args.Skip(1).ToArray()),
                "config" or "cfg" => await DispatchConfigAsync(sp, args.Skip(1).ToArray()),
                "company" or "cmp" => await DispatchCompanyAsync(sp, args.Skip(1).ToArray()),
                "keymap" or "km" => await DispatchKeymapAsync(sp, args.Skip(1).ToArray()),
                "workstate" or "ws" => DispatchWorkState(sp, args.Skip(1).ToArray()),
                "feature" or "features" => DispatchFeature(sp, args.Skip(1).ToArray()),
                "softtrigger" or "st" => await GateAsync(sp, "softtrigger", () => DispatchSoftTriggerAsync(sp, args.Skip(1).ToArray())),
                "help" or "--help" or "-h" => PrintHelp(),
                "version" or "--version" => PrintVersion(),
                _ => UnknownCommand(area)
            };
        }
        catch (Exception ex)
        {
            ConsoleFormatter.Error(ex.Message);
            if (verbose)
                Console.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    // === Keymap ===
    private static async Task<int> DispatchKeymapAsync(IServiceProvider sp, string[] args)
    {
        var cmd = sp.GetRequiredService<KeymapCommands>();
        var action = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        var rest = args.Skip(1).ToArray();

        return action switch
        {
            "list" or "ls" => cmd.List(),

            // show [配置名]：缺省 = 当前配置
            "show" or "cat" => cmd.Show(GetPositional(rest, 0)),

            "new" or "copy" => cmd.New(
                GetPositional(rest, 0) ?? throw new ArgumentException("缺少新配置名：keymap new <名> [--from <配置>]"),
                GetOption(rest, "--from")),

            "set" => cmd.Set(
                GetPositional(rest, 0) ?? throw new ArgumentException("缺少配置名：keymap set <名> --index N [字段...]"),
                GetOptionInt(rest, "--index", -1),
                GetOption(rest, "--key") is { } key ? KeymapTextParser.ParseKeyId(key) : null,
                GetOption(rest, "--event") is { } ev ? KeymapTextParser.ParseEvent(ev) : null,
                GetOption(rest, "--action") is { } act ? KeymapTextParser.ParseAction(act) : null,
                GetOption(rest, "--code") is { } code ? KeymapTextParser.ParseByte(code) : null,
                GetOption(rest, "--code-hi") is { } hi ? KeymapTextParser.ParseByte(hi) : null,
                GetOption(rest, "--mod") is { } mod ? KeymapTextParser.ParseModifier(mod) : null),

            "remove" or "rm" => cmd.Remove(
                GetPositional(rest, 0) ?? throw new ArgumentException("缺少配置名：keymap remove <名> --index N"),
                GetOptionInt(rest, "--index", -1)),

            "switch" or "sw" => cmd.Switch(
                GetPositional(rest, 0) ?? throw new ArgumentException("缺少配置名：keymap switch <名> --mode 0|1|2 [--code 0x6A] [--mod alt]"),
                GetOption(rest, "--mode") is { } m ? KeymapTextParser.ParseByte(m) : throw new ArgumentException("缺少 --mode（0=待机 1=识别 2=扩音）"),
                GetOption(rest, "--code") is { } code ? KeymapTextParser.ParseByte(code) : null,
                GetOption(rest, "--mod") is { } mod ? KeymapTextParser.ParseModifier(mod) : null),

            "rename" or "mv" => cmd.Rename(
                GetPositional(rest, 0) ?? throw new ArgumentException("用法：keymap rename <旧名> <新名>"),
                GetPositional(rest, 1) ?? throw new ArgumentException("用法：keymap rename <旧名> <新名>")),

            "delete" or "del" => cmd.Delete(
                GetPositional(rest, 0) ?? throw new ArgumentException("缺少配置名：keymap delete <名>")),

            "use" => cmd.Use(
                GetPositional(rest, 0) ?? throw new ArgumentException("缺少配置名：keymap use <名>")),

            // apply [配置名]：0x55 整表下发（回执成功才切上位机当前配置；出厂默认=恢复设备默认表）
            "apply" => await cmd.Apply(GetPositional(rest, 0)),

            _ => UnknownCommand($"keymap {action}")
        };
    }

    // === WorkState ===
    /// <summary>`workstate binding|listen`（笔端工作状态自管：触发键联动 Keymap 配置，全局键盘钩子被动监听）</summary>
    private static int DispatchWorkState(IServiceProvider sp, string[] args)
    {
        var cmd = sp.GetRequiredService<WorkStateCommands>();
        var action = args.Length > 0 ? args[0].ToLowerInvariant() : "binding";
        var rest = args.Skip(1).ToArray();

        return action switch
        {
            "binding" or "b" => cmd.Binding(GetOption(rest, "--config")),

            // listen [--secs N]：缺省=一直监听直到 Ctrl+C
            "listen" or "ln" => cmd.Listen(GetOption(rest, "--config"), GetOptionInt(rest, "--secs", 0)),

            _ => UnknownCommand($"workstate {action}")
        };
    }

    // === Feature（14 号架构提案：功能模块总开关，CLI 与 GUI 控制台同一开关） ===
    private static int DispatchFeature(IServiceProvider sp, string[] args)
    {
        var cmd = sp.GetRequiredService<FeatureCommands>();
        var action = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        var rest = args.Skip(1).ToArray();

        return action switch
        {
            "list" or "ls" => cmd.List(),

            // on/off <id>：立即持久化 configs/features.json；停用域入口拒绝并指路
            "on" or "enable" => cmd.Set(
                GetPositional(rest, 0) ?? throw new ArgumentException("缺少功能 id：feature on <airmouse|audio|seriallog>"),
                enabled: true),

            "off" or "disable" => cmd.Set(
                GetPositional(rest, 0) ?? throw new ArgumentException("缺少功能 id：feature off <airmouse|audio|seriallog>"),
                enabled: false),

            _ => UnknownCommand($"feature {action}")
        };
    }

    // === Device ===
    private static async Task<int> DispatchDeviceAsync(IServiceProvider sp, string[] args)
    {
        var cmd = sp.GetRequiredService<DeviceCommands>();
        var action = args.Length > 0 ? args[0].ToLowerInvariant() : "scan";

        return action switch
        {
            "scan" => await cmd.ScanAsync(
                GetOption(args, "--vid") is { } v ? (ushort?)ParseHex(v) : null,
                GetOption(args, "--pid") is { } p ? (ushort?)ParseHex(p) : null),

            "connect" => await cmd.ConnectAsync(
                GetOption(args, "--path"),
                GetOption(args, "--vid") is { } v ? (ushort?)ParseHex(v) : null,
                GetOption(args, "--pid") is { } p ? (ushort?)ParseHex(p) : null),

            "info" => await cmd.InfoAsync(),
            "disconnect" or "disc" => await cmd.DisconnectAsync(),

            "monitor" => await cmd.MonitorAsync(
                GetOption(args, "--rid") is { } r ? (byte?)ParseHex(r) : null,
                GetOptionInt(args, "--duration", 10)),

            "send" => await cmd.SendRawAsync(
                (byte)ParseHex(GetOptionRequired(args, "--rid")),
                GetOptionRequired(args, "--hex"),
                HasFlag(args, "--legacy")),

            "feature" => await cmd.SendFeatureAsync(
                (byte)ParseHex(GetOptionRequired(args, "--rid")),
                GetOptionRequired(args, "--hex"),
                HasFlag(args, "--legacy")),

            "ep4" => await DispatchDeviceEp4Async(cmd, args.Skip(1).ToArray()),

            _ => UnknownCommand($"device {action}")
        };
    }

    // === BLE 广播扫描 + 中心设备（PC"类基站"测试位） ===
    private static async Task<int> DispatchBleAsync(IServiceProvider sp, string[] args)
    {
        var cmd = sp.GetRequiredService<BleCommands>();
        var central = sp.GetRequiredService<BleCentralCommands>();
        var action = args.Length > 0 ? args[0].ToLowerInvariant() : "scan";
        var rest = args.Skip(1).ToArray();

        return action switch
        {
            "scan" => await cmd.ScanAsync(GetOptionInt(rest, "--duration", 5)),
            "recommend" or "rec" => await cmd.RecommendAsync(GetOptionInt(rest, "--duration", 5)),

            // —— 中心设备（GAP/GATT/SMP 整周期；手册：_share/issues/0-待处理/命令行操作与OTA技能化/03）——
            "connect" or "conn" => await central.ConnectAsync(
                GetOption(rest, "--mac"), GetOption(rest, "--name"),
                HasFlag(rest, "--public"), HasFlag(rest, "--random"),
                GetOptionInt(rest, "--timeout", 10_000), GetOptionInt(rest, "--hold", 0),
                HasFlag(rest, "--maintain")),

            "discover" or "services" => await central.DiscoverAsync(
                GetOption(rest, "--mac"), GetOption(rest, "--name"),
                HasFlag(rest, "--public"), HasFlag(rest, "--random"),
                GetOptionInt(rest, "--timeout", 10_000),
                HasFlag(rest, "--cached")),

            "read" => await central.ReadAsync(
                GetOption(rest, "--mac"), GetOption(rest, "--name"),
                GetOptionRequired(rest, "--uuid"), GetOptionInt(rest, "--timeout", 10_000)),

            "write" => await central.WriteAsync(
                GetOption(rest, "--mac"), GetOption(rest, "--name"),
                GetOptionRequired(rest, "--uuid"), GetOptionRequired(rest, "--hex"),
                HasFlag(rest, "--no-response"), GetOptionInt(rest, "--timeout", 10_000)),

            "notify" or "subscribe" => await central.NotifyAsync(
                GetOption(rest, "--mac"), GetOption(rest, "--name"),
                GetOptionRequired(rest, "--uuid"), GetOptionInt(rest, "--duration", 10),
                GetOptionInt(rest, "--timeout", 10_000)),

            "pair" => await central.PairAsync(
                GetOption(rest, "--mac"), GetOption(rest, "--name"),
                GetOption(rest, "--protect") ?? "encryption", GetOptionInt(rest, "--timeout", 10_000)),

            "unpair" => await central.UnpairAsync(
                GetOption(rest, "--mac"), GetOption(rest, "--name"), GetOptionInt(rest, "--timeout", 10_000)),

            "watch" => await central.WatchAsync(
                GetOption(rest, "--mac"), GetOption(rest, "--name"),
                GetOptionInt(rest, "--duration", 60), GetOptionInt(rest, "--timeout", 10_000)),

            "stress" => await central.StressAsync(
                GetOption(rest, "--mac"), GetOption(rest, "--name"),
                GetOptionInt(rest, "--rounds", 10), GetOptionInt(rest, "--hold", 0),
                GetOptionInt(rest, "--gap", 1), HasFlag(rest, "--no-discover"),
                HasFlag(rest, "--pair-cycle"), GetOptionInt(rest, "--allow-fail", 0),
                GetOptionInt(rest, "--timeout", 10_000),
                GetOption(rest, "--protect") ?? "encryption"),

            _ => UnknownCommand($"ble {action}")
        };
    }

    // === Device ep4 (custom_hid 无 RID 命令通道) ===
    private static async Task<int> DispatchDeviceEp4Async(DeviceCommands cmd, string[] args)
    {
        var action = args.Length > 0 ? args[0].ToLowerInvariant() : "connect";

        return action switch
        {
            "connect" => await cmd.Ep4ConnectAsync(
                GetOption(args, "--vid") is { } v ? (ushort?)ParseHex(v) : null,
                GetOption(args, "--pid") is { } p ? (ushort?)ParseHex(p) : null),

            "send" => await cmd.Ep4SendAsync(
                GetOption(args, "--cmd"),
                GetOption(args, "--mac"),
                GetOption(args, "--raw"),
                HasFlag(args, "--ble"),
                GetOptionInt(args, "--wait", 600)),

            "bind" => await cmd.Ep4BindAsync(GetOptionRequired(args, "--mac")),

            "monitor" => await cmd.Ep4MonitorAsync(GetOptionInt(args, "--duration", 5)),

            _ => UnknownCommand($"device ep4 {action}")
        };
    }

    // === Audio ===
    private static async Task<int> DispatchAudioAsync(IServiceProvider sp, string[] args)
    {
        var cmd = sp.GetRequiredService<AudioCommands>();
        var action = args.Length > 0 ? args[0].ToLowerInvariant() : "devices";

        return action switch
        {
            "devices" or "list" => await cmd.DevicesAsync(),

            "capture" or "rec" => await cmd.CaptureAsync(
                GetOption(args, "--device") ?? GetOption(args, "-d"),
                GetOptionInt(args, "--seconds", 10) == 0 ? GetOptionInt(args, "-s", 10) : GetOptionInt(args, "--seconds", 10),
                GetOption(args, "--file")),  // --output/-o 与全局输出格式选项冲突，落盘路径用 --file

            "monitor" => await cmd.MonitorAsync(
                GetOption(args, "--device") ?? GetOption(args, "-d"),
                GetOptionInt(args, "--duration", 30)),

            // 扩音渲染端：列播放设备
            "renders" or "outs" => await cmd.RendersAsync(),

            // 精确投放：把 WAV 播到指定扬声器（绕开系统默认路由）
            "play" => await cmd.PlayAsync(
                GetOption(args, "--file"),
                GetOption(args, "--out"),
                float.TryParse(GetOption(args, "--volume"), out var pv) ? Math.Clamp(pv, 0f, 1f) : 1f,
                GetOptionInt(args, "--seconds", 0)),

            // 回声相似度评分（AEC 客观评测）
            "echoscore" or "echo" => await cmd.EchoScoreAsync(
                GetOption(args, "--file"),
                GetOption(args, "--ref"),
                double.TryParse(GetOption(args, "--window"), out var w) ? w : 1.0,
                double.TryParse(GetOption(args, "--search"), out var s) ? s : 0.5),
            "volume" or "vol" => await cmd.VolumeAsync(
                GetOption(args, "--out"),
                GetOption(args, "--in"),
                GetOptionInt(args, "--set", -1) is { } s && s >= 0 ? s : null,
                HasFlag(args, "--unmute"),
                GetOptionInt(args, "--mic-set", -1) is { } m && m >= 0 ? m : null,
                HasFlag(args, "--mic-unmute")),

            // 扩音直通：录音设备 → 播放设备实时回路（端到端打通用）
            "passthrough" or "amp" => await cmd.PassthroughAsync(
                GetOption(args, "--in"),
                GetOption(args, "--out"),
                GetOptionInt(args, "--rate", 16000),
                GetOptionInt(args, "--latency", 40),
                float.TryParse(GetOption(args, "--volume"), out var vol)
                    ? Math.Clamp(vol, 0f, PencilBox.Core.Models.PassthroughStartOptions.MaxVolume) : 1.0f,
                GetOptionInt(args, "--duration", 0),
                GetOptionInt(args, "--aec", 1) != 0,
                GetOptionInt(args, "--ans", 1) != 0,
                GetOptionInt(args, "--afc", 0) != 0,
                GetOptionInt(args, "--aec-suppress", 0),   // 0 = 自动档
                GetOptionInt(args, "--ans-suppress", 0),   // 0 = 自动档
                HasFlag(args, "--aec-gate"),               // AEC 条件触发（默认关）
                GetOption(args, "--record-out"),
                GetOptionInt(args, "--hpf", 80),           // 带通下边 Hz（0=关）
                GetOptionInt(args, "--lpf", 0),            // 带通上边 Hz（0=关）
                GetOption(args, "--bridge"),               // 桥接副汇（供其他软件当已降噪麦克风）
                GetOptionInt(args, "--bridge-latency", 150),
                HasFlag(args, "--record-bridge"),          // 输出环回录制（AEC 客观评测）
                GetOption(args, "--notch"),                // 定点滤除 Hz 列表，如 1000 或 1000,2000（电机单频啸声）
                GetOptionInt(args, "--notch-q", 25)),      // notch Q（中心/带宽，越大坑越窄）

            // 延迟测量底座（box/07 §2）：脉冲互相关闭环延迟
            "latency-probe" or "probe" => await cmd.LatencyProbeAsync(
                GetOption(args, "--in"),
                GetOption(args, "--out"),
                GetOptionInt(args, "--pulses", 20),
                GetOptionInt(args, "--interval", 500),
                GetOptionInt(args, "--pulse-ms", 15),
                float.TryParse(GetOption(args, "--amplitude"), out var amp) ? Math.Clamp(amp, 0.05f, 1f) : 0.8f,
                GetOptionInt(args, "--render-latency-ms", 40),   // 仅定测试缓冲大小，不参与数值
                GetOption(args, "--save-capture"),
                GetOption(args, "--report-out")),

            // WAV 包络分析（离线；批次 1 验收②④）
            "envelope" or "env" => await cmd.EnvelopeAsync(
                GetOption(args, "--file"),
                GetOptionInt(args, "--threshold-db", -35),
                GetOptionInt(args, "--block-ms", 100),
                GetOption(args, "--csv")),

            // P2 带内 tick 统计（box/07 §2.4）
            "tickstats" or "tick" => await cmd.TickStatsAsync(
                GetOption(args, "--file"),
                double.TryParse(GetOption(args, "--step-ms"), out var step) ? step : 7.5,
                double.TryParse(GetOption(args, "--session-gap-ms"), out var gap) ? gap : 200,
                HasFlag(args, "--json")),

            _ => UnknownCommand($"audio {action}")
        };
    }

    // === Serial ===
    private static async Task<int> DispatchSerialAsync(IServiceProvider sp, string[] args)
    {
        var cmd = sp.GetRequiredService<SerialCommands>();
        var action = args.Length > 0 ? args[0].ToLowerInvariant() : "list";

        return action switch
        {
            "list" => await cmd.ListAsync(),

            // 抓日志前的自检：这个口有没有数据、能不能解出 F5（05 §3.4）
            "probe" => await cmd.ProbeAsync(
                GetOptionRequired(args, "--port"),
                GetOptionInt(args, "--baud", 1_000_000),
                GetOptionInt(args, "--seconds", 3)),

            // 抓日志：设备日志 + F5 帧 → 统一时间轴（三份文件，按大小轮转）
            "log" => await cmd.LogAsync(
                new SerialLogCliOptions
                {
                    Port = GetOption(args, "--port"),
                    Name = GetOption(args, "--name"),
                    Port2 = GetOption(args, "--port2"),
                    Name2 = GetOption(args, "--name2"),
                    Baud = GetOptionInt(args, "--baud", 1_000_000),
                    Baud2 = int.TryParse(GetOption(args, "--baud2"), out int baud2) ? baud2 : null,
                    All = HasFlag(args, "--all"),
                    NoClear = HasFlag(args, "--no-clear"),
                    Rotate = GetOption(args, "--rotate"),
                    Jsonl = GetOption(args, "--jsonl"),
                    Decode = GetOption(args, "--decode"),
                    Duration = GetOptionInt(args, "--duration", 0),
                    LogDir = GetOption(args, "--log-dir")
                },
                CancellationToken.None),

            // 读日志（AI 用）：默认只看最近一次会话（`--session all` 看全部）
            // 注：这里刻意**不用 `--all`** —— 它已被 `log` 用作"按配置起多口"，同名两义最容易踩
            "tail" => await cmd.TailAsync(
                GetOptionInt(args, "--last", 200),
                GetOption(args, "--port"),
                GetOption(args, "--domain"),
                string.Equals(GetOption(args, "--session"), "all", StringComparison.OrdinalIgnoreCase),
                GetOption(args, "--log-dir")),

            "grep" => await cmd.GrepAsync(
                GetOptionRequired(args, "--pattern"),
                GetOption(args, "--port"),
                GetOption(args, "--log-dir")),

            // [AUSTAT] 遥测解析（基站 D3 计数器链 → CSV；box/07 §2.2）
            "astat" => await cmd.AstatAsync(
                string.Equals(GetOption(args, "--session"), "all", StringComparison.OrdinalIgnoreCase),
                GetOption(args, "--port"),
                GetOption(args, "--log-dir"),
                GetOption(args, "--csv")),

            // 对拍：以统一时间轴为基准，PC 收发帧 × 设备日志 → 缺失/重查/终态/谁先谁后
            "diff" => await cmd.DiffAsync(
                GetOption(args, "--log-dir"),
                string.Equals(GetOption(args, "--session"), "all", StringComparison.OrdinalIgnoreCase),
                GetOption(args, "--port")),

            "open" => await cmd.OpenAsync(
                GetOptionRequired(args, "--port"),
                GetOptionInt(args, "--baud", 115200)),

            "send" => await cmd.SendAsync(GetOption(args, "--hex"), GetOption(args, "--text")),

            "monitor" => await cmd.MonitorAsync(GetOptionInt(args, "--duration", 30)),
            "close" => await cmd.CloseAsync(),

            _ => UnknownCommand($"serial {action}")
        };
    }

    // === Macro ===
    private static async Task<int> DispatchMacroAsync(IServiceProvider sp, string[] args)
    {
        var cmd = sp.GetRequiredService<MacroCommands>();
        var action = args.Length > 0 ? args[0].ToLowerInvariant() : "list";

        return action switch
        {
            "list" => cmd.List(),

            "run" => await cmd.RunAsync(GetOptionRequired(args, "--id"), HasFlag(args, "--loop"), GetOptionInt(args, "--count", 1)),

            "add" => await cmd.AddAsync(
                GetOptionRequired(args, "--name"),
                GetOptionRequired(args, "--keys"),
                GetOptionInt(args, "--interval", 100)),

            "delete" or "del" => await cmd.DeleteAsync(GetOptionRequired(args, "--id")),

            _ => UnknownCommand($"macro {action}")
        };
    }

    // === DSP ===
    private static async Task<int> DispatchDspAsync(IServiceProvider sp, string[] args)
    {
        var cmd = sp.GetRequiredService<DspCommands>();
        var action = args.Length > 0 ? args[0].ToLowerInvariant() : "check";

        return action switch
        {
            "check" => await cmd.CheckAsync(),
            "scripts" or "list" => cmd.Scripts(),

            "run" => await cmd.RunAsync(
                GetOptionRequired(args, "--script"),
                GetOptionRequired(args, "--input"),
                GetOption(args, "--params")),

            "analyze" => await cmd.AnalyzeAsync(
                GetOptionRequired(args, "--input"),
                GetOption(args, "--report-out") ?? GetOption(args, "-o")),

            _ => UnknownCommand($"dsp {action}")
        };
    }

    // === Config ===
    private static async Task<int> DispatchConfigAsync(IServiceProvider sp, string[] args)
    {
        var cmd = sp.GetRequiredService<ConfigCommands>();
        var action = args.Length > 0 ? args[0].ToLowerInvariant() : "get";

        return action switch
        {
            "get" => await cmd.GetAsync(GetOption(args, "--key")),
            "set" => await cmd.SetAsync(GetOptionRequired(args, "--key"), GetOptionRequired(args, "--value")),
            "path" => await cmd.PathAsync(),
            _ => UnknownCommand($"config {action}")
        };
    }


    // === Company（公司 V0.9 协议：0x03 识别 / 0x51 配对信息 / 0x52 进配对 / 0x16 升级） ===
    private static async Task<int> DispatchCompanyAsync(IServiceProvider sp, string[] args)
    {
        var cmd = sp.GetRequiredService<CompanyCommands>();
        var action = args.Length > 0 ? args[0].ToLowerInvariant() : "identify";
        var rest = args.Skip(1).ToArray();
        var port = GetOption(rest, "--port");
        var no = (byte)GetOptionInt(rest, "--no", 0);
        // USB 多实例选台（02 号多笔同升）：多支 dongle 同插时按序列号选台；空=唯一台自动 / 多台报错列名单
        cmd.TargetSerial = GetOption(rest, "--serial");

        return action switch
        {
            "identify" or "id" => await cmd.IdentifyAsync(port, GetOption(rest, "--target")),

            // 0x04 读 Git Hash（定稿 2026-10-16；内部调试指令）：串口=笔端(0x11) / USB=基站(0x00)，
            // 无线轨不开放（--target pen 仅在 USB 轨被拒并提示不支持）
            "hash" => await cmd.HashAsync(port, GetOption(rest, "--target")),

            "pair" => await DispatchCompanyPairAsync(cmd, rest, port, no),

            // 固件路径支持两种写法：位置参数（便于照抄笔端文档里的命令）或 --fw <path>
            "ota" => await cmd.OtaAsync(
                port,
                GetOption(rest, "--fw") ?? GetFirstPositional(rest)
                    ?? throw new ArgumentException("缺少固件路径：请用位置参数 <path.bin> 或 --fw <path>"),
                GetOption(rest, "--target") ?? "bs",
                GetOption(rest, "--sver") ?? "1.0.0",
                GetOptionInt(rest, "--hver", -1),
                GetOptionInt(rest, "--chunk-gap-ms", -1),
                GetOptionInt(rest, "--abort-after-block", -1)),

            "raw" => await cmd.RawAsync(port, GetOptionRequired(rest, "--hex"), GetOptionInt(rest, "--wait", 600)),

            // 0x61 内部调参（issues-pencil/01/02/03；仅串口轨笔端直连）：
            // list 全表 / get 单参 / set 单参写 / reset 恢复出厂 / export 导出宏片段 / snapshot 方案快照 A/B
            // —— 空鼠域（14 号 feature airmouse 门禁）
            "param" => await GateAsync(sp, "airmouse", () => DispatchCompanyParamAsync(sp, rest)),

            // 0x62 空鼠遥测流（笔端 06 卷；仅串口轨）：on/off 测帧率、record 录波落 CSV
            "stream" => await GateAsync(sp, "airmouse", () => DispatchCompanyStreamAsync(sp, rest)),
            "record" => await GateAsync(sp, "airmouse", () => sp.GetRequiredService<CompanyStreamCommands>().RecordAsync(
                GetOption(rest, "--port"),
                GetOptionInt(rest, "--secs", 30),
                (byte)Math.Clamp(GetOptionInt(rest, "--div", 1), 1, 255),
                GetOption(rest, "--out"))),

            // 0x62 零漂统计模式（对齐卷 06 §4.1 stillsurvey）：静置开统计→分布报告；--csv 离线分析已有录波
            "stillstats" => await GateAsync(sp, "airmouse", () => sp.GetRequiredService<CompanyStreamCommands>().StillStatsAsync(
                GetOption(rest, "--port"),
                GetOptionInt(rest, "--secs", 60),
                (byte)Math.Clamp(GetOptionInt(rest, "--div", 1), 1, 255),
                GetOption(rest, "--out"),
                Math.Max(1, GetOptionInt(rest, "--stages", 4)),
                GetOptionDouble(rest, "--gate", 0.5),
                HasFlag(rest, "--json"),
                GetOption(rest, "--csv"))),

            // 0x53 状态上行（基站把统一TX 状态槽原样搬运上行；仅 USB 轨）：
            // 拉取快照一次，--monitor N 继续监听基站主动推送 N 秒（电量/休眠等实时变化）；
            // 监测期内同步打印充电/放电计时位（充电双口径=到100%/PMU充满，产品选型对比）
            "status" => await cmd.StatusAsync(port, GetOptionInt(rest, "--monitor", 0)),

            // 0x54 统一TX 转发信封（产品级运行时命令；无 --port = BLE 中转经基站，--port = 串口直连）。
            // 动作取**首个位置参数**（选项可先于动作出现，如 `pencmd --port COM20 set-k2 1`）
            "pencmd" => await cmd.PenCmdAsync(
                port,
                GetFirstPositionalWithIndex(rest, out var actionRest) ?? "",
                GetFirstPositional(actionRest) ?? GetOption(rest, "--mode"),
                GetOption(rest, "--hex")),

            _ => UnknownCommand($"company {action}")
        };
    }

    /// <summary>`company stream on [--div N] [--secs N] [--keep] | off`（0x62 遥测流，笔端 06 卷）</summary>
    private static async Task<int> DispatchCompanyStreamAsync(IServiceProvider sp, string[] args)
    {
        var cmd = sp.GetRequiredService<CompanyStreamCommands>();
        var action = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        var rest = args.Skip(1).ToArray();

        return action switch
        {
            "on" => await cmd.StreamOnAsync(
                GetOption(rest, "--port"),
                (byte)Math.Clamp(GetOptionInt(rest, "--div", 1), 1, 255),
                GetOptionInt(rest, "--secs", 5),
                HasFlag(rest, "--keep")),
            "off" => await cmd.StreamOffAsync(GetOption(rest, "--port")),
            _ => UnknownCommand("company stream on|off")
        };
    }

    private static async Task<int> DispatchCompanyParamAsync(IServiceProvider sp, string[] args)
    {
        var cmd = sp.GetRequiredService<CompanyParamCommands>();
        var action = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        var rest = args.Skip(1).ToArray();
        var port = GetOption(rest, "--port");
        var raw = HasFlag(rest, "--raw");

        return action switch
        {
            "list" or "ls" => await cmd.ListAsync(port, raw),

            // --id 接受十进制（2）或 0x 十六进制（0x02）
            "get" => await cmd.GetAsync(port, (byte)ParseHex(GetOptionRequired(rest, "--id")), raw),

            "set" => await cmd.SetAsync(
                port,
                (byte)ParseHex(GetOptionRequired(rest, "--id")),
                float.Parse(GetOptionRequired(rest, "--value"), System.Globalization.CultureInfo.InvariantCulture),
                raw),

            // 恢复出厂（03 卷 §6）：可写 8 参（0x01~0x08，01 卷 §5+§5.1）写回出厂宏值；--only 1,3,5 子集（ID 同 --id 口径）
            "reset" => await cmd.ResetAsync(port, ParseIdList(GetOption(rest, "--only")), raw),

            // 导出宏片段（03 卷 §6 核心交付）：实读全表 + 0x03 版本 → #define 片段（零偏项不入宏只附注释；--out 存文件）
            "export" => await cmd.ExportAsync(port, GetOption(rest, "--note"), GetOption(rest, "--out")),

            // 方案快照 A/B（03 卷 §6）：仅本地 configs/airmouse/*.json，不下笔端
            "snapshot" or "snap" => await DispatchCompanyParamSnapshotAsync(cmd, rest, port),

            _ => UnknownCommand($"company param {action}")
        };
    }

    private static async Task<int> DispatchCompanyParamSnapshotAsync(
        CompanyParamCommands cmd, string[] args, string? port)
    {
        var action = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        var rest = args.Skip(1).ToArray();
        var name = GetFirstPositional(rest);

        return action switch
        {
            "save" => await cmd.SnapshotSaveAsync(port, name ?? "", GetOption(rest, "--note")),
            "list" or "ls" => await cmd.SnapshotListAsync(),
            "show" => await cmd.SnapshotShowAsync(name ?? GetOptionRequired(rest, "--name")),
            "use" => await cmd.SnapshotUseAsync(port, name ?? GetOptionRequired(rest, "--name"), HasFlag(rest, "--raw")),
            "delete" or "del" => await cmd.SnapshotDeleteAsync(name ?? GetOptionRequired(rest, "--name")),
            _ => UnknownCommand($"company param snapshot {action}")
        };
    }

    /// <summary>`--only 1,3,5`（或 0x 十六进制、空格分隔）→ ID 列表；缺省 null=全部</summary>
    private static List<byte>? ParseIdList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        return text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => (byte)ParseHex(t))
            .ToList();
    }

    private static async Task<int> DispatchCompanyPairAsync(CompanyCommands cmd, string[] args, string? port, byte no)
    {
        var action = args.Length > 0 ? args[0].ToLowerInvariant() : "read";

        return action switch
        {
            "read" => await cmd.PairReadAsync(port, no),

            "write" => await cmd.PairWriteAsync(
                port,
                no,
                GetOptionRequired(args, "--mac"),
                GetOption(args, "--name")),

            "enter" => await cmd.PairEnterAsync(),

            // 工厂快配对：串口读笔端 → USB 写基站 → 回读校验
            "factory" => await cmd.PairFactoryAsync(port, no),

            // 0x51 PARAM=03 清绑定（仅串口/笔端轨：笔端删 bond → 进配对名视窗 60s）
            "clear" => await cmd.PairClearAsync(port),

            _ => UnknownCommand($"company pair {action}")
        };
    }

    // === SoftTrigger（13 号动作库键盘族；纯软件域零设备依赖；门禁=feature softtrigger） ===
    private static async Task<int> DispatchSoftTriggerAsync(IServiceProvider sp, string[] args)
    {
        var cmd = sp.GetRequiredService<SoftTriggerCommands>();
        var action = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        var rest = args.Skip(1).ToArray();

        return action switch
        {
            "list" or "ls" => cmd.List(),

            "add" => cmd.Add(
                GetPositional(rest, 0) ?? throw new ArgumentException("缺少名称：softtrigger add <名> --trigger ALT+F2 --keys \"F2@100,ALT+L\""),
                GetOption(rest, "--trigger") ?? "F24",
                GetOption(rest, "--keys") ?? throw new ArgumentException("缺少序列：--keys \"F2@100,ALT+L\""),
                GetOption(rest, "--mode"),
                GetOptionInt(rest, "--step-interval", 0),
                GetOptionInt(rest, "--repeat-interval", 200),
                GetOption(rest, "--note"),
                rest.Contains("--disabled"),
                GetOptionInt(rest, "--priority", 0)),

            "remove" or "rm" or "delete" => cmd.Remove(
                GetPositional(rest, 0) ?? throw new ArgumentException("用法：softtrigger remove <名>")),

            "on" => cmd.SetEnabled(GetPositional(rest, 0) ?? throw new ArgumentException("用法：softtrigger on <名>"), true),
            "off" => cmd.SetEnabled(GetPositional(rest, 0) ?? throw new ArgumentException("用法：softtrigger off <名>"), false),

            // run：不经钩子直接回放（无 F17 物理键时的验证路径；键击进前台窗口）
            "run" or "fire" => await cmd.RunAsync(
                GetPositional(rest, 0) ?? throw new ArgumentException("用法：softtrigger run <名> [--count N]"),
                GetOptionInt(rest, "--count", 1)),

            // send：裸发送键序列（不带定义试手感；保留段 F13~F24 拒发防回环）
            "send" => cmd.Send(
                GetOption(rest, "--keys") ?? throw new ArgumentException("缺少序列：send --keys \"CTRL+SHIFT+S\""),
                GetOptionInt(rest, "--interval", 60)),

            // selftest：进程内拉引擎 → 注入 F24 → 钩子捕获 → 回放 F7 → 事件断言（CLI-first 零设备验收路径）
            "selftest" or "test" => await cmd.Selftest(),
            _ => UnknownCommand($"softtrigger {action}")
        };
    }

    // === Feature gate helpers（14 号：可选功能停用 → 域入口拒绝并指路，退出码 1） ===
    private static async Task<int> GateAsync(IServiceProvider sp, string featureId, Func<Task<int>> run) =>
        FeatureCommands.EnsureEnabled(sp.GetRequiredService<PencilBox.Core.Features.IFeatureToggleService>(), featureId)
            ? await run()
            : 1;

    private static int Gate(IServiceProvider sp, string featureId, Func<int> run) =>
        FeatureCommands.EnsureEnabled(sp.GetRequiredService<PencilBox.Core.Features.IFeatureToggleService>(), featureId)
            ? run()
            : 1;

    // === Help / Version ===
    private static int PrintHelp()
    {
        Console.WriteLine("""
        PencilBox CLI - USB HID / Audio / Serial / DSP debugging tool

        USAGE:
            pencilbox <area> <action> [options]

        AREAS:
            device   HID device management (scan, connect, monitor, ep4 send)
            ble      BLE 广播扫描 + 中心设备（PC"类基站"测试位）
                     scan/recommend                扫描广播 / 推荐笔端 MAC
                     connect --mac|--name [--hold N] [--maintain]   GAP 建连探针
                     discover --mac|--name [--cached]              GATT 全表枚举（默认 Uncached）
                     read/write --uuid .. [--hex ..] [--no-response] 特征读写
                     notify --uuid .. --duration 10                订阅通知收流
                     pair/unpair [--protect encryption]            SMP 配对/退配
                     watch --duration 60                           断连-回连事件时间线
                     stress --rounds 10 [--pair-cycle] [--no-discover] [--allow-fail 0]
                                                                   反复重连/配对压测（GAP/SMP 稳定性）
                     专项手册：_share/issues/0-待处理/命令行操作与OTA技能化/03
            audio    USB Audio capture (devices, capture, monitor)【可选功能：feature off audio 可整域关】
                     renders   列播放设备（扩音渲染端）
                     volume    端点音量自检/设置（--set 35 --unmute / --mic-set 80 --mic-unmute）
                               "跑了没声"先看这里：多半是端点静音或音量 0
                     passthrough 扩音直通：录音→扬声器实时回路（amp 别名）
                               [--in <id|名称子串>] [--out <id|名称子串>] [--rate 16000]
                               [--latency 40] [--volume 0~24（软增益，内置防破音限制器）]
                               [--aec 1] [--ans 1] [--afc 0]  处理链开关（信号顺序：AEC→ANS→AFC）
                               [--aec-suppress N] [--ans-suppress N]  手动调参 dB（0/缺省=自动档）
                               [--aec-gate]  AEC 条件触发：参考静音满 500ms 旁路 AEC（实验，默认关）
                               [--hpf 80] [--lpf 0]  带通降噪 Hz（0=关；HPF 切机械抖动/低频传导）
                               [--notch 1000,2000] [--notch-q 25]  定点滤除（电机单频啸声；频率用 dsp run --script fft_analysis 实测）
                               [--bridge <设备>] [--bridge-latency 150]  桥接副汇：后级信号复投第二设备（虚拟声卡）
                               [--duration 0(=Ctrl+C 停)]
                     probe   延迟探测：播扫频脉冲串→采集同录→互相关闭环延迟（自校准：--in/--out 同本机）
                               [--pulses 20] [--interval 500] [--pulse-ms 15] [--render-latency-ms 40 仅定测试缓冲大小，不参与扣减]
                               [--save-capture out.wav] [--report-out report.json]  数值=物理闭环原值（不扣减）
                     envelope  WAV 包络分析：整体电平/逐块信封/首声时刻/尾音（--file x.wav [--csv]）
                     tickstats P2 tick 统计：--file ticks.csv → 漂移扣除/p50/p95/重传率（[--json]）
            serial   串口：设备日志 + F5 协议帧 → 统一时间轴【可选功能：feature off seriallog 可整域关】
                    list    列端口（带类型标注，避开调试器虚拟口/蓝牙口）
                    probe   试读 N 秒：有没有数据 / 能不能解出 F5
                    log     抓日志（双口可同时；三份文件；按大小轮转）
                    tail    读主日志（默认只看最近一次会话）
                    grep    在最近一次会话里按正则捞行
                    astat   解析 [AUSTAT] 遥测行（基站 D3 计数器链）→ 概览+CSV（--csv out.csv）
                    diff    对拍摘要：PC 收发帧 × 设备日志（缺失/重查/终态/谁先谁后）
                    open/send/monitor/close  （通用收发口子，非主线）
            macro    Key macro management (list, run, add, delete)
            dsp      DSP signal processing (check, scripts, run, analyze)
            config   Configuration management (get, set, path)
            company  公司 V0.9 协议（0xFF02 命令通道；OTA 在此：company ota）
                     USB 轨通用选项 --serial <SN>：多支 dongle 同插时按序列号选台
                     （缺省=唯一台自动选中；多台未指定→报错列名单，禁静默取第一台；支持唯一前缀）
            keymap   快捷键映射配置（本地 configs/keymap/；按键表+S1 切换表；0x55 下发=apply，pc-issues/05）
                     list / show [配置]                            配置清单 / 表格内容（缺省当前配置）
                     new <名> [--from <配置>]                      复制创建（缺省复制出厂默认）并设为当前
                     set <名> --index N [--key K3] [--event click]
                         [--action keyboard] [--code 0x4B] [--code-hi 0] [--mod alt]
                                                                   改/追加条目（存储按 K1→K6 规范排序，索引会变）
                     switch <名> --mode 1|2 [--code 0x6A] [--mod alt]
                                                                   改 S1 切换表档位热键（ON/SPK=扩音开关；IDLE 内部行拒改）
                     apply [配置]                                  本机立即生效 + 0x55 整表尽力下发基站
                                                                   （未连接/未响应=[WARN] 仅本机生效、退出码 0；
                                                                   出厂默认=恢复设备默认表）
                     remove <名> --index N                         删除条目（K2 长按/长放=会话边界契约行拒删）
                     rename <旧> <新> / delete <名> / use <名>     改名 / 删除 / 设为当前
            workstate 笔端工作状态自管（全局键盘钩子监听 Keymap 触发键，识别×扩音两轴）
                     binding [--config 名]                          从 Keymap 配置推导四键绑定
                     listen [--config 名] [--secs N]                装钩实时打印状态转移（Ctrl+C 停）
            feature 功能模块总开关（14 号架构提案；GUI 功能控制台同一开关，configs/features.json）
                     list                                           功能清单（ID/默认/当前/管辖域）
                     on <id> / off <id>                             启用/停用（id 见 list；停用后 GUI 入口隐藏、
                                                                    管辖 CLI 域拒绝并指路；核心域不受管）
            softtrigger 软触发（13 号动作库键盘族；纯软件域零设备依赖，本机键击回放）【可关：feature off softtrigger】
                     list                                           定义清单（触发键→序列/优先级/启用/备注）
                     add <名> --trigger ALT+F2 --keys "F2@100,ALT+L"
                                                                    新建：触发键=全键盘池±修饰（F13~F16 工作状态保留除外）；
                          [--mode once|toggle]                      防回环=动态预留律（启用集内触发键∩动作键=∅，冲突点名拒）
                          [--repeat-interval 200] [--priority 0]    步语法=修饰+主键[@步前延时ms]，周期穿插步间
                          [--step-interval N]（旧参数：摊给未@步）
                          [--note ".."] [--disabled]
                     on/off/remove <名>                             启用/停用/删除（Desktop 引擎随重载生效；
                                                                    启用冲突=动态预留律拒绝并指出冲突定义）
                     run <名> [--count N]                           手动试射：不经钩子直接回放（键击进前台窗口）
                     send --keys "CTRL+SHIFT+S" [--interval 60]     裸发送键序列（试手感；F13~F16 拒发；
                                                                    撞启用定义触发键=[WARN] 会真实触发）
                     selftest                                       回环自证：注入 F24→钩子捕获→回放 F7→事件断言
                                                                    （零设备零人工，CLI-first 验收路径）
                    identify | pair read/write/enter/factory/clear | ota | raw | status
                    pair clear --port COMx   0x51/03 清绑定（仅串口轨，笔端删 bond 进配对视窗）
                    status [--monitor N]     0x53 状态快照（电量/设备状态，仅 USB 轨）+ 监听推送
                    pencmd   统一TX 信封 0x54（无 --port = BLE 中转经基站；--port = 串口直连）：
                             set-k2 <1|2> | query-k2 | send --hex
                    param    0x61 内部调参（issues-pencil/01/02/03，仅串口轨 --port COMx 笔端直连）【可关：feature off airmouse】：
                             list | get --id N | set --id N --value F     [--raw 显 VALUE4 hex]
                                                                          （11 参=01 卷 §5+§5.1；0x09/0x0A 只读、0x0B 写1=固化/写2=清除）
                             reset [--only 1,3,5]                         可写 8 参（0x01~0x08）写回出厂宏值（逐参回显校验）
                             export [--note ".."] [--out 文件]             实读全表+版本 → #define 回填片段（零偏项只附注释不入宏）
                             snapshot save <名> [--note ".."] | list | show <名> | use <名> | delete <名>
                                                                          方案快照 A/B（本地 configs/airmouse/，不下笔端）
                             参数 RAM-only：立即生效、掉电回出厂宏值；越界/未知 ID 由笔端裁决
                    stream   0x62 遥测流开关（笔端 06 卷，仅串口轨）：
                             on [--div N] [--secs 5] [--keep]     START→实测帧率→自动 STOP（--keep 保持推流）
                             off [--port COMx]                    STOP（幂等；测完自拆不留后台流）
                    record   [--secs 30] [--div 1] [--out f.csv] --port COMx
                                                              START→收 N 秒→STOP→CSV 落盘（原码直出）+
                                                              # meta: fps/dropped/div/fw 尾注；Ctrl+C 提前收束不丢数据
                    stillstats 零漂统计模式（对齐卷 06 §4.1，笔**静置**开统计→分布报告直出）：
                             [--secs 60] [--stages 4] [--gate 0.5] --port COMx
                                                              双通道（raw=去偏差前全量 / rate=补偿后残余）+
                                                              三方对照 0x07~0x0A + 温漂回归 + 分位 P5/中位/P95；
                                                              剔除 >20% 提示重跑；CSV 照落盘复用 record 格式
                             --csv f.csv [--stages N] [--json] 离线模式：直接分析已有录波（不出串口）

        GLOBAL OPTIONS:
            --verbose / -v    Verbose logging
            --output <fmt>    Output format: table (default) / json / quiet

        EXAMPLES:
            pencilbox device scan
            pencilbox device scan --vid 0x1133
            pencilbox device connect --vid 0x1133 --pid 0x0100
            pencilbox device monitor --duration 10
            pencilbox device ep4 send --cmd A1
            pencilbox device ep4 send --cmd A4 --ble       (auto-find SunVote pen MAC via BLE)
            pencilbox company identify                     (0x03，USB 报基站）
            pencilbox company identify --port COM3         (0x03，串口报笔端）
            pencilbox company hash                         (0x04 读 Git Hash，USB 报基站；40 位 hex ASCII)
            pencilbox company hash --port COM3             (0x04 读笔端 hash，仅插线串口；无线轨不支持)
            pencilbox company pair read --no 0
            pencilbox company pair write --no 0 --mac AA:BB:CC:DD:EE:FF --name SunVoteR1:AABBCCDDEEFF
            pencilbox company pair enter                   (0x52，等待 01 → 02/03）
            pencilbox company pair factory --port COM3 --no 0   (串口读笔端 → USB 写基站 → 回读)
            pencilbox company ota --fw app.bin --target bs --sver 1.0.0
            pencilbox company ota --port COM20 --target pen app.bin --sver 0.11.16 --hver 6
                    [固件：--fw <bin> 或直接位置参数 <bin>] [--chunk-gap-ms 5] [--abort-after-block 1]
            pencilbox company raw --hex "F5 AA AA 04 61 01 03 00" [--port COM3] [--wait 600]
            pencilbox company status [--monitor 60]                  (0x53 电量/设备状态快照+推送，USB 轨)
            pencilbox company pair clear --port COM20                (0x51/03 清绑定，笔端进配对视窗 60s)
            pencilbox company param list --port COM23                (0x61 全表读：ID/名称/当前值/范围/单位)
            pencilbox company param set --port COM23 --id 1 --value 0.35   (调参→立即生效试手感)
            pencilbox company param get --port COM23 --id 2 --raw    (单参读+VALUE4 hex 对拍)
            pencilbox company param reset --port COM23               (可写 8 参回出厂宏值，不重启即复位)
            pencilbox company param export --port COM23 --note "手感定稿 v3" --out macros.c  (宏回填片段)
            pencilbox company param snapshot save A --port COM23     (快照 A → use B → use A 互切对比)
            pencilbox company stream on --port COM23 --secs 5        (0x62 START→实测帧率→自动 STOP)
            pencilbox company record --port COM23 --secs 30 --out r.csv   (录波→CSV+meta；配 tools/airmouse_metrics.py)
            pencilbox company stillstats --port COM23 --secs 60     (笔静置零漂统计→分布报告+三方对照；CSV 照落盘)
            pencilbox company stillstats --csv r.csv --stages 6     (离线分析已有录波：分阶段中位/分位+温漂)
            pencilbox company pencmd set-k2 1                       (BLE 中转：USB 信封 → 基站 → 当前笔，0x21 方式1)
            pencilbox company pencmd query-k2                       (BLE 中转查询 0x42；笔未连接回 STATUS=02)
            pencilbox company pencmd --port COM20 set-k2 1          (串口直连形态，笔端本地终结)
            pencilbox ble scan --duration 5
            pencilbox ble recommend --duration 5
            pencilbox ble connect --mac C6:56:00:3A:4A:F1 --hold 5
            pencilbox ble discover --name SunVote-R1            (GATT 全表：服务/特征/属性/句柄)
            pencilbox ble read --mac AA:BB:CC:DD:EE:FF --uuid 2A00
            pencilbox ble write --uuid 0000FFF4-... --hex "01 02" --no-response
            pencilbox ble notify --uuid 2A19 --duration 10      (电量推送收流)
            pencilbox ble pair --mac AA:BB:CC:DD:EE:FF --protect encryption
            pencilbox ble watch --mac AA:BB:CC:DD:EE:FF --duration 60   (断连-回连时间线)
            pencilbox ble stress --mac AA:BB:CC:DD:EE:FF --rounds 20 --pair-cycle --allow-fail 1
            pencilbox audio devices
            pencilbox audio capture -d <id> -s 5 --file out.wav
            pencilbox serial list
            pencilbox serial probe --port COM6 --baud 1000000 --seconds 3
            pencilbox serial log --port COM6 --baud 1000000 --name DONGLE [--duration 60]
            pencilbox serial log --port COM7 --name PENCIL --port2 COM6 --name2 DONGLE [--no-clear]
            pencilbox serial log --port COM7 --name PENCIL --port2 COM23 --name2 PENCIL_UART1 --baud 1000000 --baud2 115200
            pencilbox serial log --all [--rotate 10M:10] [--jsonl on|off] [--decode f5|off]
            pencilbox serial tail --last 200 [--port DONGLE] [--domain ota_usb] [--session all]
            pencilbox serial grep --pattern "block|DONE|cpu_reset" [--port DONGLE]
            pencilbox serial diff [--port DONGLE] [--session all]
            pencilbox serial open --port COM3 --baud 115200
            pencilbox serial send --hex "4A 4C 00 00"
            pencilbox dsp check
            pencilbox dsp scripts
            pencilbox dsp run --script fft_analysis --input audio.wav
            pencilbox dsp analyze --input audio.wav --output report.json
            pencilbox config get
            pencilbox config set --key LogLevel --value Debug
        """);
        return 0;
    }

    private static int PrintVersion()
    {
        Console.WriteLine("PencilBox CLI v3.1.0 (.NET 10)");
        return 0;
    }

    private static int UnknownCommand(string cmd)
    {
        ConsoleFormatter.Error($"Unknown command: {cmd}");
        Console.WriteLine("Run 'pencilbox help' for usage.");
        return 1;
    }

    // === Argument parsing helpers ===

    private static string? GetOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
                return args[i + 1];
        }
        return null;
    }

    private static string GetOptionRequired(string[] args, string name)
    {
        var val = GetOption(args, name);
        if (val == null)
            throw new ArgumentException($"Missing required option: {name}");
        return val;
    }

    private static int GetOptionInt(string[] args, string name, int defaultValue)
    {
        var val = GetOption(args, name);
        return int.TryParse(val, out int result) ? result : defaultValue;
    }

    private static double GetOptionDouble(string[] args, string name, double defaultValue)
    {
        var val = GetOption(args, name);
        return double.TryParse(val, out double result) ? result : defaultValue;
    }

    private static bool HasFlag(string[] args, string name)
    {
        return args.Contains(name);
    }

    /// <summary>
    /// 取第一个"位置参数"（不以 `--` 开头，且不是某个 `--选项` 的取值）。
    /// 目的：让两端文档里给出的命令可以**原样照抄**，例如笔端 06 §T7-0：
    /// `company ota --port COM20 --target pen "&lt;path&gt;/app.bin" --sver 0.11.16 --hver 6`
    /// </summary>
    private static string? GetFirstPositional(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--"))
            {
                // 选项后若紧跟非选项 token，视为该选项的取值 → 跳过，避免把 COM20/pen 当成位置参数
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                    i++;
                continue;
            }
            return args[i];
        }
        return null;
    }

    /// <summary>取第 <paramref name="ordinal"/> 个（0-based）位置参数（"选项取值"不算位置参数）。</summary>
    private static string? GetPositional(string[] args, int ordinal)
    {
        int seen = 0;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--"))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                    i++;
                continue;
            }
            if (seen == ordinal)
                return args[i];
            seen++;
        }
        return null;
    }

    /// <summary>取首个位置参数，并经 <paramref name="restAfter"/> 返回其**之后**的剩余参数（供子动作再解析）。</summary>
    private static string? GetFirstPositionalWithIndex(string[] args, out string[] restAfter)
    {
        restAfter = args;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--"))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                    i++;
                continue;
            }
            restAfter = args.Skip(i + 1).ToArray();
            return args[i];
        }
        return null;
    }

    private static int ParseHex(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        return int.Parse(s, System.Globalization.NumberStyles.HexNumber);
    }
}
