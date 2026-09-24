using Microsoft.Extensions.Logging;
using PencilBox.Core.Communication;
using PencilBox.Core.SerialScope;

namespace PencilBox.Infrastructure.Serial;

/// <summary>
/// **一个串口 = 一个读侧实例**（05 §2「双串口监听：每端口一个读线程，互不阻塞」）。
///
/// 职责边界：
/// - 只用串口的**读**能力（复用既有实现，不另写一套 IO）；
/// - 字节 → 日志行 的加工全在 Core 的 <see cref="SerialScopePipeline"/>（纯逻辑、可离线单测）；
/// - **绝不拉 DTR/RTS**：R1 板 `DTR→RESET` 低有效，误拉会复位笔端
///   （坑点速查 §七；<see cref="SerialPortSettings"/> 已默认 false，此处**不再覆盖**）。
///
/// 两种打开模式（06 M1）：
/// - **旁路（tap）模式**：目标口已被 <see cref="SerialPortHub"/> 打开（命令面在读 MAC / OTA）⇒ **不再自己开口**，
///   挂订阅旁听——收（设备→PC）与写（PC→设备，写前广播）**全部字节进同一时间轴**（`↑`/`↓` 由帧头方向位自动判定）；
/// - **独占模式**（原行为）：口没人用 ⇒ 自己 new <see cref="SerialTransport"/> 打开（此开口也注册进 hub，
///   后来的命令面可复用——反过来解决"日志先开、读 MAC 后到"的抢口）。
/// </summary>
public sealed class SerialLogReader : IAsyncDisposable
{
    /// <summary>收到一行（含分域与解码结果）</summary>
    public event EventHandler<LogLine>? LineEmitted;

    private SerialTransport? _transport;
    private readonly SerialScopePipeline _pipeline;
    private readonly ILogger<SerialLogReader> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ISerialPortHub? _hub;
    private readonly string _portName;
    private readonly int _baudRate;

    private IDisposable? _tapSubscription;
    private bool _tapped;

    /// <summary>旁路/独占模式下本会话是否持有 hub 引用（释放时减；归零才动物理口）</summary>
    private bool _holdsReference;

    private bool _disposed;

    public SerialLogReader(
        string label,
        string portName,
        int baudRate,
        bool decodeFrames,
        ILoggerFactory loggerFactory,
        ISerialPortHub? hub = null)
    {
        Label = label;
        _portName = portName;
        _baudRate = baudRate;
        _hub = hub;
        _loggerFactory = loggerFactory;
        _pipeline = new SerialScopePipeline(label, decodeFrames: decodeFrames);
        _logger = loggerFactory.CreateLogger<SerialLogReader>();
    }

    /// <summary>端口标签（`PENCIL` / `DONGLE`…；每行 `[NAME]` 用）</summary>
    public string Label { get; }

    /// <summary>物理串口名（`COM6`…）</summary>
    public string PhysicalPort => _portName;

    /// <summary>是否已打开（旁路模式 = 已挂上共享口）</summary>
    public bool IsOpen => _tapped || _transport is { IsConnected: true };

    /// <summary>打开端口并开始收行；失败返回 <c>false</c>（已产出一行 `&lt;ERROR&gt;` 说明原因）</summary>
    public async Task<bool> StartAsync()
    {
        // ① 旁路模式：口已被其他功能打开（读 MAC / OTA / 上一个日志会话）⇒ 只挂订阅，不再抢句柄（06 M1）
        if (_hub is { } hub && hub.IsPortOpen(_portName))
        {
            var openBaud = hub.BaudRateOf(_portName) ?? _baudRate;
            if (openBaud != _baudRate)
                EmitSystemLine(
                    $"⚠️ {_portName} 已由 [{hub.OwnersOf(_portName)}] 以 {openBaud} 波特率打开（本页请求 {_baudRate}）——" +
                    $"旁听跟随共享口波特率，数据可能不可读；如需 {_baudRate} 请先停掉占用方的会话");

            // 旁听也**持引用保活**：命令面会话结束（对方释放）后，本会话仍可持续收流，直到自己停止。
            // 用共享口的现行波特率加引用（永不冲突）。
            _holdsReference = await hub.OpenAsync(_portName, openBaud, SerialPortOwners.Scope);

            _tapSubscription = hub.Subscribe(_portName, OnBytes, OnBytes);   // 收/写都进同一时间轴
            _tapped = true;

            EmitSystemLine(
                $"🔗 {_portName} 已由 [{hub.OwnersOf(_portName)}] 以 {openBaud} 打开 —— 本页挂载为**旁路**：" +
                "读 MAC / OTA 的收发将实时出现在下方（↓=PC 发出 ↑=设备应答），不与对方抢口");
            _logger.LogInformation("串口日志旁路启动：{Label} -> {Port}（共享 @{Baud}，持有者 [{Owners}]）",
                Label, _portName, openBaud, hub.OwnersOf(_portName));
            return true;
        }

        // ② 独占模式（原行为）：口没人用 ⇒ 自己开（此开口注册进 hub，命令面后续可复用）
        var settings = new SerialPortSettings { BaudRate = _baudRate };
        // DTR/RTS 保持 SerialPortSettings 的默认 false —— 这是笔端不被复位的前提（红线）
        if (_hub is { } h)
        {
            try
            {
                if (!await h.OpenAsync(_portName, _baudRate, SerialPortOwners.Scope))
                {
                    EmitSystemLine($"<ERROR> 打开串口失败：{_portName} @{_baudRate}（被占用？口不存在？）");
                    return false;
                }

                _tapSubscription = h.Subscribe(_portName, OnBytes, OnBytes);
                _tapped = true;
                _holdsReference = true;
                _logger.LogInformation("串口日志独占启动（经 hub）：{Label} -> {Port} @{Baud}", Label, _portName, _baudRate);
                return true;
            }
            catch (SerialPortConflictException ex)
            {
                EmitSystemLine($"<ERROR> {ex.Message}");
                return false;
            }
        }

        // hub 未注入（旧路径，理论上仅遗留测试用）：直接自己 new（与历史行为一致）
        var transport = new SerialTransport(_loggerFactory.CreateLogger<SerialTransport>());
        if (!await transport.ConnectAsync(_portName, settings))
        {
            await transport.DisposeAsync();
            EmitSystemLine($"<ERROR> 打开串口失败：{_portName} @{_baudRate}（被占用？口不存在？）");
            return false;
        }

        _transport = transport;
        _transport.DataReceived += OnDataReceived;
        _logger.LogInformation("串口日志监听已启动：{Label} -> {Port} @{Baud}", Label, _portName, _baudRate);
        return true;
    }

    /// <summary>
    /// 停止并**吐出残留半行**（会话收尾不丢最后一行）。
    /// 只退订；**hub 引用释放统一在 <see cref="DisposeAsync"/>**（以 <see cref="_holdsReference"/> 键控）——
    /// M1 曾在 StopAsync 置 <c>_tapped=false</c> 后让 DisposeAsync 再判 <c>if (_tapped)</c>，
    /// 结果 hub.CloseAsync 被整体跳过 ⇒ 口与读线程永久泄漏（06 验收"双口停止"问题的伴生缺陷）。
    /// </summary>
    public async Task StopAsync()
    {
        if (_disposed)
            return;

        foreach (var line in _pipeline.Flush(DateTimeOffset.Now))
            LineEmitted?.Invoke(this, line);

        // 旁路/独占（经 hub）一律只退订，不动物理口（谁开的谁关）；hub 引用计数收尾在 DisposeAsync
        _tapSubscription?.Dispose();
        _tapSubscription = null;
        _tapped = false;

        if (_transport != null)
            await _transport.DisconnectAsync();
    }

    /// <summary>hub 旁路回调：收（设备→PC）与写（PC→设备）字节都喂同一管线——方向由帧头自动判（↑/↓）</summary>
    private void OnBytes(ReadOnlyMemory<byte> data)
    {
        var timestamp = DateTimeOffset.Now;

        try
        {
            foreach (var line in _pipeline.Feed(timestamp, data.Span))
                LineEmitted?.Invoke(this, line);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加工串口数据失败：{Label}", Label);
            EmitSystemLine($"<ERROR> 加工数据失败：{ex.Message}");
        }
    }

    private void OnDataReceived(object? sender, Core.Communication.SerialDataReceivedEventArgs e)
    {
        // 同一主机时钟、即收即投：跨端口因而可按时间排序（双口对拍的前提，05 §2）
        OnBytes((ReadOnlyMemory<byte>)e.Data);
    }

    private void EmitSystemLine(string text)
        => LineEmitted?.Invoke(this, new LogLine(
            DateTimeOffset.Now, Label, LogSource.Text, LogDomain.Sys, text, text));

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        _tapSubscription?.Dispose();
        _tapSubscription = null;
        _tapped = false;

        // 本会话持过引用（旁路保活 / 独占开口）：释放；归零才动物理口。
        // ⚠️ 以 _holdsReference 键控，**不得**依赖 _tapped（StopAsync 已将其清零）
        if (_holdsReference && _hub is { } h)
        {
            _holdsReference = false;
            await h.CloseAsync(_portName, SerialPortOwners.Scope);
        }

        if (_transport != null)
        {
            _transport.DataReceived -= OnDataReceived;
            await _transport.DisposeAsync();
        }
    }
}
