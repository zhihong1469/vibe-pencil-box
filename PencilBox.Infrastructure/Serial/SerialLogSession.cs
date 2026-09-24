using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PencilBox.Core.Communication;
using PencilBox.Core.SerialScope;

namespace PencilBox.Infrastructure.Serial;

/// <summary>一次抓日志的入参（由 CLI 组装；默认值即"与参考实现对齐"的那套策略）</summary>
public sealed class SerialLogSessionRequest
{
    /// <summary>要抓的端口（双口可选可同时，05 §3.5）</summary>
    public List<SerialScopePortSpec> Ports { get; init; } = new();

    /// <summary>日志目录（绝对路径）</summary>
    public string LogDir { get; init; } = string.Empty;

    /// <summary>轮转策略</summary>
    public LogRotationPolicy Rotation { get; init; } = new();

    /// <summary>是否同时写 JSONL（机读）</summary>
    public bool Jsonl { get; init; } = true;

    /// <summary>是否把 F5 帧解码成人类可读（关 = 原样 HEX）</summary>
    public bool DecodeFrames { get; init; } = true;

    /// <summary>启动时是否清空原日志（默认清空，与参考实现一致）</summary>
    public bool ClearOnStart { get; init; } = true;

    /// <summary>运行秒数；0 = 不限（靠 Ctrl+C / 取消令牌退出）</summary>
    public int DurationSeconds { get; init; }

    /// <summary>状态/进度回显（交给交互层，Infrastructure 不直接写 Console）</summary>
    public Action<string>? Report { get; init; }

    /// <summary>
    /// 每一行的**实时回调**（UI 的实时打印区用；CLI 不传）。
    /// 在落盘泵线程上调用 —— 回调方自己负责切到 UI 线程与限流。
    /// </summary>
    public Action<LogLine>? OnLine { get; init; }
}

/// <summary>
/// **抓日志会话**：起 N 个 <see cref="SerialLogReader"/> → 单条落盘泵 → 三份日志。
///
/// 为什么要有"单条落盘泵"：多个读线程并发写同一批文件会互相踩（交错顺序也没意义），
/// 故统一经一条队列串行落盘 —— 与参考实现 `serial-chat` 的 `LogAssembler` 单线程模型一致。
/// </summary>
public sealed class SerialLogSession
{
    /// <summary>进度心跳间隔（秒）</summary>
    private const int ProgressIntervalSeconds = 5;

    private readonly ISerialTransport _enumerator;
    private readonly ISerialPortHub? _hub;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SerialLogSession> _logger;

    public SerialLogSession(ISerialTransport enumerator, ILoggerFactory loggerFactory, ISerialPortHub? hub = null)
    {
        _enumerator = enumerator;
        _hub = hub;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SerialLogSession>();
    }

    /// <summary>跑一次抓日志会话；返回进程退出码（0 = 正常收尾）</summary>
    public async Task<int> RunAsync(SerialLogSessionRequest request, CancellationToken cancellationToken)
    {
        var report = request.Report ?? (_ => { });

        if (request.Ports.Count == 0)
        {
            report("没有要抓的端口：请给 --port（或用 --all 走配置）");
            return 1;
        }

        var resolved = await ResolvePortsAsync(request.Ports, report);
        if (resolved.Count == 0)
        {
            report("没有可用的串口，未启动");
            return 1;
        }

        var labels = resolved.Select(r => r.Label).ToList();

        var assembler = new SessionAssembler();
        using var queue = new BlockingCollection<LogLine>(new ConcurrentQueue<LogLine>());

        // ⚠️ 顺序讲究：**先起端口、成功了再清日志**。
        // 反过来的话，`--port` 打错一个字就会把上一轮的好日志清掉却什么也没抓到（数据白丢）。
        var readers = new List<SerialLogReader>();
        foreach (var spec in resolved)
        {
            // hub 注入读侧：口被命令面占用时自动走旁路（06 M1），没人用时独占开口（口注册进 hub，命令面可复用）
            var reader = new SerialLogReader(spec.Label, spec.Port, spec.BaudRate, request.DecodeFrames,
                _loggerFactory, _hub);
            reader.LineEmitted += (_, line) => Enqueue(queue, line);
            readers.Add(reader);
        }

        int started = 0;
        foreach (var reader in readers)
        {
            if (await reader.StartAsync())
                started++;
        }

        if (started == 0)
        {
            foreach (var reader in readers)
                await reader.DisposeAsync();
            report("所有端口都打不开（COM 口独占：确认没有其它程序/命令在占用）—— 原日志**未被清空**");
            return 1;
        }

        if (request.ClearOnStart)
        {
            SerialLogWriter.Clear(request.LogDir, labels);
            report("已清空原日志（要保留历史请加 --no-clear）");
        }
        else
        {
            report("保留原日志（--no-clear）：本次内容**追加**在旧内容之后");
        }

        using var writer = new SerialLogWriter(request.LogDir, labels, request.Rotation, request.Jsonl);
        writer.WriteSessionStart(DateTimeOffset.Now);

        var pump = Task.Run(() =>
        {
            foreach (var line in queue.GetConsumingEnumerable())
            {
                writer.Write(line);
                assembler.Add(line);
                request.OnLine?.Invoke(line);
            }
        }, CancellationToken.None);

        report($"📄 日志目录：{writer.LogDir}");
        report($"   主交错 {SerialLogWriter.MainFileName} · 每端口 <NAME>.log · " +
               $"{(request.Jsonl ? SerialLogWriter.JsonlFileName : "JSONL 已关")}");
        report($"🔌 监听中：{string.Join("  ", resolved.Select(r => $"{r.Label}->{r.Port}@{r.BaudRate}"))}");
        report(request.DurationSeconds > 0
            ? $"⏳ 运行 {request.DurationSeconds}s 后自动退出"
            : "⏳ 运行中（Ctrl+C 退出）");

        await PumpProgressAsync(request, assembler, resolved, report, cancellationToken);

        await ShutdownAsync(readers, queue, pump);

        report($"✅ 收尾完成：共 {assembler.TotalCount} 行");
        foreach (var spec in resolved)
        {
            var (lines, frames) = assembler.CountOf(spec.Label);
            report($"   [{spec.Label}] {lines} 行（其中 F5 帧 {frames} 条） → {writer.PathFor(spec.Label)}");
        }
        report($"   主日志：{writer.MainPath}");

        return 0;
    }

    /// <summary>进度心跳：直到取消 / 到达 --duration</summary>
    private static async Task PumpProgressAsync(
        SerialLogSessionRequest request,
        SessionAssembler assembler,
        List<ResolvedPort> resolved,
        Action<string> report,
        CancellationToken cancellationToken)
    {
        var deadline = request.DurationSeconds > 0
            ? DateTimeOffset.Now.AddSeconds(request.DurationSeconds)
            : DateTimeOffset.MaxValue;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        while (!linked.IsCancellationRequested && DateTimeOffset.Now < deadline)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(ProgressIntervalSeconds), linked.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (linked.IsCancellationRequested)
                break;

            var parts = resolved.Select(r =>
            {
                var (lines, frames) = assembler.CountOf(r.Label);
                return $"[{r.Label}] {lines}行/{frames}帧";
            });
            report($"… 共 {assembler.TotalCount} 行 · {string.Join(" · ", parts)}");
        }
    }

    /// <summary>停读 → 让落盘泵把队列写完 → 收尾（顺序不能反，否则会丢尾部日志）</summary>
    private static async Task ShutdownAsync(
        List<SerialLogReader> readers, BlockingCollection<LogLine> queue, Task pump)
    {
        foreach (var reader in readers)
            await reader.StopAsync();

        foreach (var reader in readers)
            await reader.DisposeAsync();

        queue.CompleteAdding();
        await pump;
    }

    /// <summary>读线程推队列；收尾阶段队列已关闭 ⇒ 丢弃即可（不回抛到读线程）</summary>
    private static void Enqueue(BlockingCollection<LogLine> queue, LogLine line)
    {
        try
        {
            queue.Add(line);
        }
        catch (Exception)
        {
            // Queue 已 CompleteAdding / 已释放：会话正在收尾，忽略
        }
    }

    /// <summary>
    /// 端口解析：`auto` 自动挑一个未被占用的口。
    /// 挑法照参考实现 `auto_detect_port` 的优先级：USB 转串口桥 &gt; 未知 &gt; 蓝牙 &gt; 调试器虚拟口。
    /// </summary>
    private async Task<List<ResolvedPort>> ResolvePortsAsync(
        List<SerialScopePortSpec> specs, Action<string> report)
    {
        var ports = await _enumerator.EnumeratePortsAsync();   // 只枚举，不占用端口

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ResolvedPort>();

        foreach (var spec in specs)
        {
            var label = string.IsNullOrWhiteSpace(spec.Name) ? spec.Port : spec.Name;
            var port = spec.Port;

            if (string.IsNullOrWhiteSpace(port) || port.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                var pick = PickAuto(ports, used);
                if (pick == null)
                {
                    report($"⚠️ [{label}] 未找到可用串口 —— 请在配置里写死 `port`");
                    continue;
                }

                report($"⚠️ [{label}] auto → {pick.PortName}（**按类型猜的**，要对拍请在配置里写死端口）");
                port = pick.PortName;
            }

            if (!used.Add(port))
            {
                report($"⚠️ [{label}] {port} 已被前面的端口占用，跳过");
                continue;
            }

            result.Add(new ResolvedPort(label, port, spec.BaudRate));
        }

        return result;
    }

    /// <summary>按"优先 USB 转串口桥、其次调试器虚拟口、避开蓝牙"挑一个未被占用的口（策略见 <see cref="SerialPortClassifier.Preference"/>）</summary>
    private static SerialPortInfo? PickAuto(IReadOnlyList<SerialPortInfo> ports, HashSet<string> used)
        => ports
            .Where(p => !used.Contains(p.PortName))
            .OrderBy(p => SerialPortClassifier.Preference(SerialPortClassifier.Classify(p.Description)))
            .ThenBy(p => p.PortName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    private sealed record ResolvedPort(string Label, string Port, int BaudRate);
}
