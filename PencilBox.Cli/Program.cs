using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using PencilBox.Cli.Commands;
using PencilBox.Cli.Output;
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

        var sp = services.BuildServiceProvider();

        try
        {
            var area = args[0].ToLowerInvariant();

            return area switch
            {
                "device" or "dev" => await DispatchDeviceAsync(sp, args.Skip(1).ToArray()),
                "ble" => await DispatchBleAsync(sp, args.Skip(1).ToArray()),
                "audio" or "aud" => await DispatchAudioAsync(sp, args.Skip(1).ToArray()),
                "serial" or "ser" => await DispatchSerialAsync(sp, args.Skip(1).ToArray()),
                "macro" => await DispatchMacroAsync(sp, args.Skip(1).ToArray()),
                "dsp" => await DispatchDspAsync(sp, args.Skip(1).ToArray()),
                "config" or "cfg" => await DispatchConfigAsync(sp, args.Skip(1).ToArray()),
                "company" or "cmp" => await DispatchCompanyAsync(sp, args.Skip(1).ToArray()),
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
                HasFlag(args, "--aec-gate")),              // AEC 条件触发（默认关）

            // 延迟测量底座（box/07 §2）：脉冲互相关闭环延迟
            "latency-probe" or "probe" => await cmd.LatencyProbeAsync(
                GetOption(args, "--in"),
                GetOption(args, "--out"),
                GetOptionInt(args, "--pulses", 20),
                GetOptionInt(args, "--interval", 500),
                GetOptionInt(args, "--pulse-ms", 15),
                float.TryParse(GetOption(args, "--amplitude"), out var amp) ? Math.Clamp(amp, 0.05f, 1f) : 0.8f,
                GetOptionInt(args, "--render-latency-ms", 40),
                HasFlag(args, "--raw"),                    // 不扣渲染缓冲，看原始闭环
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

        return action switch
        {
            "identify" or "id" => await cmd.IdentifyAsync(port, GetOption(rest, "--target")),
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

            // 0x53 状态上行（基站把统一TX 状态槽原样搬运上行；仅 USB 轨）：
            // 拉取快照一次，--monitor N 继续监听基站主动推送 N 秒（电量/休眠等实时变化）
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
            audio    USB Audio capture (devices, capture, monitor)
                     renders   列播放设备（扩音渲染端）
                     volume    端点音量自检/设置（--set 35 --unmute / --mic-set 80 --mic-unmute）
                               "跑了没声"先看这里：多半是端点静音或音量 0
                     passthrough 扩音直通：录音→扬声器实时回路（amp 别名）
                               [--in <id|名称子串>] [--out <id|名称子串>] [--rate 16000]
                               [--latency 40] [--volume 0~24（软增益，内置防破音限制器）]
                               [--aec 1] [--ans 1] [--afc 0]  处理链开关（信号顺序：AEC→ANS→AFC）
                               [--aec-suppress N] [--ans-suppress N]  手动调参 dB（0/缺省=自动档）
                               [--aec-gate]  AEC 条件触发：参考静音满 500ms 旁路 AEC（实验，默认关）
                               [--duration 0(=Ctrl+C 停)]
                     probe   延迟探测：播扫频脉冲串→采集同录→互相关闭环延迟（自校准：--in/--out 同本机）
                               [--pulses 20] [--interval 500] [--pulse-ms 15] [--render-latency-ms 40]
                               [--raw] [--save-capture out.wav] [--report-out report.json]
                     envelope  WAV 包络分析：整体电平/逐块信封/首声时刻/尾音（--file x.wav [--csv]）
                     tickstats P2 tick 统计：--file ticks.csv → 漂移扣除/p50/p95/重传率（[--json]）
            serial   串口：设备日志 + F5 协议帧 → 统一时间轴
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
                    identify | pair read/write/enter/factory/clear | ota | raw | status
                    pair clear --port COMx   0x51/03 清绑定（仅串口轨，笔端删 bond 进配对视窗）
                    status [--monitor N]     0x53 状态快照（电量/设备状态，仅 USB 轨）+ 监听推送
                    pencmd   统一TX 信封 0x54（无 --port = BLE 中转经基站；--port = 串口直连）：
                             set-k2 <1|2> | query-k2 | send --hex

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
