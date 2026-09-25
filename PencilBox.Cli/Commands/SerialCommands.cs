// <copyright file="SerialCommands.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Text.RegularExpressions;
using PencilBox.Cli.Output;
using PencilBox.Core.Communication;
using PencilBox.Core.SerialScope;
using PencilBox.Infrastructure.Serial;

namespace PencilBox.Cli.Commands;

/// <summary>`serial log` 的原始命令行选项（解析留在 Program，语义留在这里）</summary>
public sealed class SerialLogCliOptions
{
    /// <summary>端口 1（`--port`）</summary>
    public string? Port { get; init; }

    /// <summary>端口 1 的标签（`--name`；缺省用端口名）</summary>
    public string? Name { get; init; }

    /// <summary>端口 2（`--port2`；给了就是**双口同时**）</summary>
    public string? Port2 { get; init; }

    /// <summary>端口 2 的标签（`--name2`）</summary>
    public string? Name2 { get; init; }

    /// <summary>波特率（`--baud`）。**默认 1M**：我们设备的日志口就用 1M（05 §1）</summary>
    public int Baud { get; init; } = 1_000_000;

    /// <summary>端口 2 的波特率（`--baud2`；不给则与端口 1 相同）。两路口速率可以不同</summary>
    public int? Baud2 { get; init; }

    /// <summary>用 `config/serial.json` 预置的多口（`--all`）</summary>
    public bool All { get; init; }

    /// <summary>保留历史日志（`--no-clear`；默认清空）</summary>
    public bool NoClear { get; init; }

    /// <summary>轮转 `10M:10`（`--rotate`）</summary>
    public string? Rotate { get; init; }

    /// <summary>`on|off`（`--jsonl`）</summary>
    public string? Jsonl { get; init; }

    /// <summary>`f5|off`（`--decode`）</summary>
    public string? Decode { get; init; }

    /// <summary>定时退出秒数（`--duration`；0=不限）</summary>
    public int Duration { get; init; }

    /// <summary>日志目录（`--log-dir`）</summary>
    public string? LogDir { get; init; }
}

/// <summary>
/// 串口面命令（05 §3.4）。
///
/// 定位提醒（05 §1.1 / §5.5）：**不做"通用串口助手"** —— 收任意 HEX 那是市面工具的事；
/// 这一面的价值是"把设备的两路文本日志 + F5 协议帧组装成人与 AI 都能读的统一时间轴"。
/// </summary>
public class SerialCommands
{
    private readonly ISerialTransport _serialTransport;
    private readonly SerialLogSession _logSession;
    private readonly SerialScopeDiffService _diffService;

    public SerialCommands(
        ISerialTransport serialTransport,
        SerialLogSession logSession,
        SerialScopeDiffService diffService)
    {
        _serialTransport = serialTransport;
        _logSession = logSession;
        _diffService = diffService;
    }

    // ================= list / probe =================

    /// <summary>`serial list`：列端口 + **类型标注**（避免选到调试器虚拟口 / 蓝牙口，05 §3.5）</summary>
    public async Task<int> ListAsync()
    {
        var ports = await _serialTransport.EnumeratePortsAsync();
        ConsoleFormatter.Info($"找到 {ports.Count} 个串口");

        ConsoleFormatter.PrintTable(ports,
            ("Port", p => p.PortName),
            ("类型", p => SerialPortClassifier.Text(SerialPortClassifier.Classify(p.Description))),
            ("Description", p => p.Description));

        foreach (var port in ports)
        {
            var role = SerialPortClassifier.Classify(port.Description);
            if (SerialPortClassifier.SelectionWarning(role) is { } warning)
                ConsoleFormatter.Info($"  ⚠️ {port.PortName}：{warning}");
        }

        return 0;
    }

    /// <summary>
    /// `serial probe`：试读若干秒，报告"**有没有数据** / **能不能解析出 F5 帧**"——
    /// 抓日志前先确认"口选对了、波特率对了"，省得抓半天空文件（05 §3.4）。
    /// </summary>
    public async Task<int> ProbeAsync(string portName, int baudRate, int seconds)
    {
        var ports = await _serialTransport.EnumeratePortsAsync();
        var info = ports.FirstOrDefault(p => string.Equals(p.PortName, portName, StringComparison.OrdinalIgnoreCase));
        var role = SerialPortClassifier.Classify(info?.Description);

        ConsoleFormatter.Info($"[PROBE] {portName} @{baudRate}，试读 {seconds}s" +
                              $"（类型：{SerialPortClassifier.Text(role)}）");
        if (SerialPortClassifier.SelectionWarning(role) is { } warning)
            ConsoleFormatter.Info($"  ⚠️ {warning}");

        var pipeline = new SerialScopePipeline(portName);
        int lines = 0, frames = 0;
        var domains = new Dictionary<string, int>(StringComparer.Ordinal);
        var samples = new List<string>();

        void OnData(object? sender, PencilBox.Core.Communication.SerialDataReceivedEventArgs e)
        {
            foreach (var line in pipeline.Feed(DateTimeOffset.Now, e.Data))
            {
                lines++;
                if (line.Source == LogSource.Frame)
                    frames++;
                domains[line.Domain] = domains.GetValueOrDefault(line.Domain) + 1;
                if (samples.Count < 5)
                    samples.Add(line.Decoded);
            }
        }

        var settings = new SerialPortSettings { BaudRate = baudRate };
        _serialTransport.DataReceived += OnData;

        try
        {
            if (!await _serialTransport.ConnectAsync(portName, settings))
            {
                ConsoleFormatter.Error($"打开串口失败：{portName}（被占用？口不存在？）");
                return 1;
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, seconds)));
        }
        finally
        {
            _serialTransport.DataReceived -= OnData;
            await _serialTransport.DisconnectAsync();     // COM 口独占：探完即放（坑点 §七）
        }

        if (lines == 0)
        {
            ConsoleFormatter.Error($"试读 {seconds}s **无任何数据** —— 检查：口选对了吗？波特率对吗？设备在跑吗？");
            return 1;
        }

        ConsoleFormatter.Success($"收到 {lines} 行（其中 F5 帧 {frames} 条）");
        ConsoleFormatter.Info("  域分布：" + string.Join("  ", domains.OrderByDescending(d => d.Value)
            .Select(d => $"{d.Key}={d.Value}")));
        ConsoleFormatter.Info($"  能否解析 F5：{(frames > 0 ? "能" : "本次未见 F5 帧")}");

        foreach (var sample in samples)
            ConsoleFormatter.Info($"  样本：{sample}");

        return 0;
    }

    // ================= log =================

    /// <summary>`serial log`：抓日志（双口可选可同时，三份文件，按大小轮转）</summary>
    public async Task<int> LogAsync(SerialLogCliOptions options, CancellationToken cancellationToken)
    {
        List<SerialScopePortSpec> ports;
        string logDir;
        LogRotationPolicy rotation;
        bool jsonl;
        bool decodeFrames;
        bool clearOnStart;

        if (options.All)
        {
            var configPath = SerialScopePaths.DefaultConfigPath;
            var config = SerialScopeConfigStore.Load(configPath, out var error);

            if (config == null)
            {
                ConsoleFormatter.Error(error!);
                if (!File.Exists(configPath))
                {
                    SerialScopeConfigStore.WriteSample(configPath);
                    ConsoleFormatter.Info($"已生成样例配置，请按需改端口/标签后再跑：{configPath}");
                }
                return 1;
            }

            ports = config.Ports;
            logDir = SerialScopePaths.ResolveLogDir(options.LogDir ?? config.LogDir);
            rotation = config.Rotation.ToPolicy();
            jsonl = config.Jsonl;
            decodeFrames = config.DecodeFrames;
            clearOnStart = config.ClearOnStart;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(options.Port))
            {
                ConsoleFormatter.Error("请给 --port COMx（双口请再加 --port2 COMx，或用 --all 走配置）");
                return 1;
            }

            ports = new List<SerialScopePortSpec>
            {
                new() { Name = options.Name ?? options.Port, Port = options.Port, BaudRate = options.Baud }
            };
            if (!string.IsNullOrWhiteSpace(options.Port2))
                ports.Add(new SerialScopePortSpec
                {
                    Name = options.Name2 ?? options.Port2,
                    Port = options.Port2,
                    BaudRate = options.Baud2 ?? options.Baud
                });

            logDir = SerialScopePaths.ResolveLogDir(options.LogDir);
            rotation = new LogRotationPolicy();
            jsonl = true;
            decodeFrames = true;
            clearOnStart = true;
        }

        // 命令行显式项覆盖配置（显式 > 配置 > 默认）
        if (options.Rotate is { Length: > 0 })
        {
            if (!LogRotationPolicy.TryParse(options.Rotate, out var parsed))
            {
                ConsoleFormatter.Error($"--rotate 格式非法：{options.Rotate}（应为 10M:10 或 10485760:10）");
                return 1;
            }
            rotation = parsed;
        }
        if (TryParseSwitch(options.Jsonl, out bool jsonlOn))
            jsonl = jsonlOn;
        if (TryParseSwitch(options.Decode, out bool decodeOn))
            decodeFrames = decodeOn;
        if (options.NoClear)
            clearOnStart = false;

        var request = new SerialLogSessionRequest
        {
            Ports = ports,
            LogDir = logDir,
            Rotation = rotation,
            Jsonl = jsonl,
            DecodeFrames = decodeFrames,
            ClearOnStart = clearOnStart,
            DurationSeconds = options.Duration,
            Report = ConsoleFormatter.Info
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Console.CancelKeyPress += OnCancel;                       // Ctrl+C：干净收尾（把尾部日志写完再退）
        try
        {
            return await _logSession.RunAsync(request, cts.Token);
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
        }

        void OnCancel(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;                                      // 不让进程被直接杀掉，先收尾
            cts.Cancel();
        }
    }

    /// <summary>`serial tail`：读主日志（默认只看**最近一次会话**，`--session all` 看全部）</summary>
    public Task<int> TailAsync(int last, string? portFilter, string? domainFilter, bool all, string? logDir)
    {
        var path = SerialLogFileReader.ResolveMainPath(logDir);
        if (!File.Exists(path))
        {
            ConsoleFormatter.Error($"日志文件不存在：{path}（先跑 `pencilbox serial log`）");
            return Task.FromResult(1);
        }

        var rows = SerialLogFileReader.ReadAllLinesShared(path);
        int totalInFile = rows.Count;

        if (!all)
            rows = SerialLogFileReader.SliceAfterLastSessionStart(rows);

        rows = SerialLogFileReader.FilterByPort(rows, portFilter);

        if (!string.IsNullOrWhiteSpace(domainFilter))
            rows = rows.Where(r => r.Contains($"][{domainFilter}] ", StringComparison.Ordinal)).ToList();

        var shown = last > 0 && rows.Count > last ? rows.Skip(rows.Count - last).ToList() : rows;
        foreach (var row in shown)
            Console.WriteLine(row);

        // 摘要走 stderr：stdout 留给"数据"本身，便于管道/Agent 直接消费（05 §1.1「面向 AI」）
        Console.Error.WriteLine(
            $"[serial tail] 本次会话 {rows.Count} 行 / 显示 {shown.Count} 行（文件共 {totalInFile} 行）；{path}" +
            (all ? "" : "；只看最近一次会话（--session all 可看全部）"));

        return Task.FromResult(0);
    }

    /// <summary>
    /// `serial diff`：**对拍摘要**（05 §4 S4）—— 以统一时间轴为基准，把 PC 收发帧与设备日志对齐，
    /// 给"缺失 / 重查 / 终态 / 谁先谁后"。
    /// </summary>
    public Task<int> DiffAsync(string? logDir, bool allSessions, string? portFilter)
    {
        try
        {
            var diff = _diffService.Compare(logDir, allSessions, portFilter);
            Console.WriteLine(diff.ToText());

            // 无缺失、无重查、无违例 ⇒ 退出码 0，便于脚本/Agent 直接判成败
            var clean = diff.MissingPcSend.Count == 0
                        && diff.MissingDeviceDone.Count == 0
                        && diff.Duplicated.Count == 0
                        && diff.OutOfOrder.Count == 0
                        && diff.FrameCount > 0
                        && diff.Blocks.Count > 0;

            return Task.FromResult(clean ? 0 : 1);
        }
        catch (FileNotFoundException ex)
        {
            ConsoleFormatter.Error(ex.Message);
            return Task.FromResult(1);
        }
    }

    /// <summary>`serial grep`：在最近一次会话里按正则捞行（如 `block|DONE|cpu_reset`）</summary>
    public Task<int> GrepAsync(string pattern, string? portFilter, string? logDir)
    {
        var path = SerialLogFileReader.ResolveMainPath(logDir);
        if (!File.Exists(path))
        {
            ConsoleFormatter.Error($"日志文件不存在：{path}（先跑 `pencilbox serial log`）");
            return Task.FromResult(1);
        }

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch (ArgumentException ex)
        {
            ConsoleFormatter.Error($"--pattern 不是合法正则：{ex.Message}");
            return Task.FromResult(1);
        }

        var rows = SerialLogFileReader.SliceAfterLastSessionStart(SerialLogFileReader.ReadAllLinesShared(path));
        rows = SerialLogFileReader.FilterByPort(rows, portFilter);

        var matched = rows.Where(r => regex.IsMatch(r)).ToList();
        foreach (var row in matched)
            Console.WriteLine(row);

        Console.Error.WriteLine($"[serial grep] 命中 {matched.Count} 行 / 本次会话 {rows.Count} 行；{path}");

        return Task.FromResult(0);
    }

    // ================= astat =================

    /// <summary>
    /// `serial astat`：从主日志解析基站 [AUSTAT] 遥测行（D3 计数器链 1s 汇总，Dongle/04 §5.2），
    /// 输出计数概览与可选 CSV（时间戳,端口,水位,各计数器）——批次 1 验收①与"每刀看水位曲线"的数据源。
    /// 前置：`serial log` 已抓到含 [AUSTAT] 的会话。
    /// </summary>
    public Task<int> AstatAsync(bool allSessions, string? portFilter, string? logDir, string? csvOut)
    {
        List<LogLine> lines;
        try
        {
            lines = SerialLogFileReader.ReadSessionLines(logDir, allSessions, portFilter);
        }
        catch (FileNotFoundException ex)
        {
            ConsoleFormatter.Error(ex.Message);
            return Task.FromResult(1);
        }

        var samples = new List<AustatSample>();
        foreach (var line in lines)
            if (AustatParser.TryParse(line, out var s))
                samples.Add(s);

        if (samples.Count == 0)
        {
            ConsoleFormatter.Error($"本次会话 {lines.Count} 行中没有 [AUSTAT] 行——确认基站已刷入 D3 固件且 serial log 在抓。" +
                                   $"可用 `serial grep --pattern AUSTAT` 原样看行。");
            return Task.FromResult(1);
        }

        var last = samples[^1];
        ConsoleFormatter.Info($"[AUSTAT] {samples.Count} 行（{samples[0].Timestamp:HH:mm:ss} ~ {last.Timestamp:HH:mm:ss}，端口 {last.Port}）");
        ConsoleFormatter.PrintObject(new
        {
            Last = $"{last.Timestamp:HH:mm:ss.fff}",
            Watermark = $"{last.WatermarkBytes}B（min-max {last.WatermarkMinBytes}~{last.WatermarkMaxBytes}）",
            last.CbufFull,
            last.AllocFail,
            last.UsbFull,
            last.SeqJump,
            Conceal = $"f/e/skip = {last.ConcealFrames}/{last.ConcealEvents}/{last.ConcealSkip}",
            PeakWatermarkB = samples.Max(s => s.WatermarkMaxBytes),
            TotalSeqJump = samples.Sum(s => s.SeqJump),
            TotalConcealEvents = samples.Sum(s => s.ConcealEvents),
            TotalConcealSkip = samples.Sum(s => s.ConcealSkip)
        });

        if (csvOut is { Length: > 0 })
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(csvOut));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var rows = new List<string>
            {
                "timestamp,port,wl_bytes,wl_min,wl_max,cbuf_full,alloc_fail,usb_full,seq_jump,conceal_frames,conceal_events,conceal_skip"
            };
            rows.AddRange(samples.Select(s =>
                $"{s.Timestamp:yyyy-MM-dd HH:mm:ss.fff},{s.Port},{s.WatermarkBytes},{s.WatermarkMinBytes},{s.WatermarkMaxBytes}," +
                $"{s.CbufFull},{s.AllocFail},{s.UsbFull},{s.SeqJump},{s.ConcealFrames},{s.ConcealEvents},{s.ConcealSkip}"));
            File.WriteAllLines(csvOut, rows);
            ConsoleFormatter.Info($"CSV 已写：{csvOut}");
        }
        return Task.FromResult(0);
    }

    // ================= 既有：通用收发（保留，非本工具主线） =================

    /// <summary>`serial open`（通用终端口子，05 §5.5 明确**不是**本工具的发力点）</summary>
    public async Task<int> OpenAsync(string portName, int baudRate)
    {
        var settings = new SerialPortSettings { BaudRate = baudRate };
        if (await _serialTransport.ConnectAsync(portName, settings))
        {
            ConsoleFormatter.Success($"已打开 {portName} @ {baudRate}bps（DTR/RTS 未施加）");
            return 0;
        }

        ConsoleFormatter.Error($"打开 {portName} 失败（COM 口独占）");
        return 1;
    }

    /// <summary>`serial send`</summary>
    public async Task<int> SendAsync(string? hex, string? text)
    {
        if (!_serialTransport.IsConnected)
        {
            ConsoleFormatter.Error("串口未打开");
            return 1;
        }

        if (!string.IsNullOrEmpty(hex))
            await _serialTransport.SendHexAsync(hex);
        else if (!string.IsNullOrEmpty(text))
            await _serialTransport.SendTextAsync(text);
        else
        {
            ConsoleFormatter.Error("请给 --hex 或 --text");
            return 1;
        }

        ConsoleFormatter.Success("已发送");
        return 0;
    }

    /// <summary>`serial monitor`</summary>
    public async Task<int> MonitorAsync(int duration)
    {
        if (!_serialTransport.IsConnected)
        {
            ConsoleFormatter.Error("串口未打开");
            return 1;
        }

        ConsoleFormatter.Info($"监视串口 {duration}s…");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(duration));

        void OnData(object? sender, PencilBox.Core.Communication.SerialDataReceivedEventArgs e)
        {
            var hex = Convert.ToHexString(e.Data);
            Console.WriteLine($"[RX {e.Data.Length}B] HEX: {hex}");
        }

        _serialTransport.DataReceived += OnData;
        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 到点退出
        }
        finally
        {
            _serialTransport.DataReceived -= OnData;
        }

        ConsoleFormatter.Info("监视结束");
        return 0;
    }

    /// <summary>`serial close`</summary>
    public async Task<int> CloseAsync()
    {
        await _serialTransport.DisconnectAsync();
        ConsoleFormatter.Success("已关闭");
        return 0;
    }

    // ================= 内部 =================

    /// <summary>`on|off|true|false|1|0|yes|no` ⇒ 开关；非法值按"不改"处理</summary>
    private static bool TryParseSwitch(string? text, out bool value)
    {
        value = false;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        switch (text.Trim().ToLowerInvariant())
        {
            case "on" or "true" or "1" or "yes" or "y":
                value = true;
                return true;
            case "off" or "false" or "0" or "no" or "n":
                value = false;
                return true;
            default:
                return false;
        }
    }
}
