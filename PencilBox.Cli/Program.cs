using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using PencilBox.Cli.Commands;
using PencilBox.Cli.Output;
using PencilBox.Core.Services;
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
        services.AddSingleton<DeviceCommands>();
        services.AddSingleton<AudioCommands>();
        services.AddSingleton<SerialCommands>();
        services.AddSingleton<MacroCommands>();
        services.AddSingleton<DspCommands>();
        services.AddSingleton<ConfigCommands>();

        var sp = services.BuildServiceProvider();

        try
        {
            var area = args[0].ToLowerInvariant();

            return area switch
            {
                "device" or "dev" => await DispatchDeviceAsync(sp, args.Skip(1).ToArray()),
                "audio" or "aud" => await DispatchAudioAsync(sp, args.Skip(1).ToArray()),
                "serial" or "ser" => await DispatchSerialAsync(sp, args.Skip(1).ToArray()),
                "macro" => await DispatchMacroAsync(sp, args.Skip(1).ToArray()),
                "dsp" => await DispatchDspAsync(sp, args.Skip(1).ToArray()),
                "config" or "cfg" => await DispatchConfigAsync(sp, args.Skip(1).ToArray()),
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
                GetOptionRequired(args, "--hex")),

            "feature" => await cmd.SendFeatureAsync(
                (byte)ParseHex(GetOptionRequired(args, "--rid")),
                GetOptionRequired(args, "--hex")),

            _ => UnknownCommand($"device {action}")
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
                GetOption(args, "--output") ?? GetOption(args, "-o")),

            "monitor" => await cmd.MonitorAsync(
                GetOption(args, "--device") ?? GetOption(args, "-d"),
                GetOptionInt(args, "--duration", 30)),

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
                GetOption(args, "--output") ?? GetOption(args, "-o")),

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

    // === Help / Version ===
    private static int PrintHelp()
    {
        Console.WriteLine("""
        PencilBox CLI - USB HID / Audio / Serial / DSP debugging tool

        USAGE:
            pencilbox <area> <action> [options]

        AREAS:
            device   HID device management (scan, connect, monitor, send)
            audio    USB Audio capture (devices, capture, monitor)
            serial   Serial port communication (list, open, send, monitor)
            macro    Key macro management (list, run, add, delete)
            dsp      DSP signal processing (check, scripts, run, analyze)
            config   Configuration management (get, set, path)

        GLOBAL OPTIONS:
            --verbose / -v    Verbose logging
            --output <fmt>    Output format: table (default) / json / quiet

        EXAMPLES:
            pencilbox device scan
            pencilbox device scan --vid 0x1133
            pencilbox device connect --vid 0x1133 --pid 0x0100
            pencilbox device monitor --duration 10
            pencilbox audio devices
            pencilbox audio capture -d <id> -s 5 -o out.wav
            pencilbox serial list
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

    private static int ParseHex(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        return int.Parse(s, System.Globalization.NumberStyles.HexNumber);
    }
}
