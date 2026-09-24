namespace PencilBox.Core.Communication;

/// <summary>串口共享的参与者命名（06 §4.3：冲突提示要能"点名谁在用"）</summary>
public static class SerialPortOwners
{
    /// <summary>命令面：读 MAC（0x51）/ OTA 串口轨（0x16）等 company 会话（经 <c>SharedSerialTransport</c>）</summary>
    public const string Command = "command";

    /// <summary>日志面：串口页 / CLI <c>serial log</c> 的**独占模式**开口（06 M1；旁路模式不开口、不占引用）</summary>
    public const string Scope = "serial-scope";
}

/// <summary>
/// 同口异波特率冲突（06 §4.3）：同一物理口同一时刻只能有一个波特率——
/// 后到者要求不同波特率时抛本异常，消息**点名现占用者**，不静默改频。
/// </summary>
public sealed class SerialPortConflictException : InvalidOperationException
{
    public string PortName { get; }
    public int OpenBaudRate { get; }
    public int RequestedBaudRate { get; }

    /// <summary>现占用者（多参与者逗号拼接，如 <c>command, serial-scope</c>）</summary>
    public string Owners { get; }

    public SerialPortConflictException(string portName, int openBaudRate, int requestedBaudRate, string owners)
        : base($"串口 {portName} 已被 [{owners}] 以 {openBaudRate} 波特率打开，与本次请求 {requestedBaudRate} 冲突" +
               "（同一物理口同一时刻只能有一个波特率；如需换波特率，请先停掉占用方的会话）")
    {
        PortName = portName;
        OpenBaudRate = openBaudRate;
        RequestedBaudRate = requestedBaudRate;
        Owners = owners;
    }
}

/// <summary>
/// 单个物理串口的 IO 能力（由 Infrastructure 提供实现；<see cref="SerialPortHub"/> 只依赖本接口 ⇒ 纯逻辑可单测）。
/// 一个实例 = 一个物理口句柄；实现负责 DTR/RTS 纪律与 UMDF 虚拟口兜底（06 §4.1）。
/// </summary>
public interface ISerialPortIo
{
    /// <summary>物理口是否处于打开状态</summary>
    bool IsOpen { get; }

    /// <summary>打开物理口（同 <see cref="ISerialTransport.ConnectAsync"/> 的纪律：DTR/RTS 默认不施加）</summary>
    Task<bool> OpenAsync(string portName, int baudRate, CancellationToken cancellationToken = default);

    /// <summary>关闭物理口（幂等）</summary>
    Task CloseAsync();

    /// <summary>写原始字节</summary>
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>收到字节（读线程上触发）</summary>
    event EventHandler<ReadOnlyMemory<byte>>? Received;
}

/// <summary>
/// **进程内串口网关**（06 §4）：COM 口在 OS 层独占，但同一进程内应当"一个物理口 = 一个句柄"——
///
/// - **多读者**：收到的字节广播给全部订阅者（命令帧装配 / OTA 帧装配 / 日志时间轴各自独立装配，互不消费）；
/// - **收发全可见**：PC 写出的字节在物理写**之前**同步广播 <c>onWritten</c>（保证日志里 `↓` 帧先于设备应答 `↑` 入轴）；
/// - **引用计数**：按 (端口, 参与者) 计引用，全部释放才关物理口——"谁先开、谁后开"不再互抢/互断。
///
/// 写仲裁（活动会话锁）属 M2（06 §5）：M1 语义与现状等价（命令面一问一答、拉模式 OTA 本身串行）。
/// </summary>
public interface ISerialPortHub
{
    /// <summary>该物理口当前是否（被任何人）打开</summary>
    bool IsPortOpen(string portName);

    /// <summary>该口的当前波特率（未打开为 <c>null</c>）</summary>
    int? BaudRateOf(string portName);

    /// <summary>该口的参与者列表（逗号拼接；未打开为 <c>null</c>）——冲突提示"点名谁在用"用</summary>
    string? OwnersOf(string portName);

    /// <summary>
    /// 打开（或复用已打开的）物理口。同口异波特率 ⇒ <see cref="SerialPortConflictException"/>；
    /// 物理打开失败（占用/口不存在）返回 <c>false</c>（与 <see cref="ISerialTransport.ConnectAsync"/> 口径一致）。
    /// </summary>
    /// <param name="portName">物理口名（大小写不敏感）</param>
    /// <param name="baudRate">波特率</param>
    /// <param name="owner">参与者名（见 <see cref="SerialPortOwners"/>；同参与者重复 Open 幂等）</param>
    Task<bool> OpenAsync(string portName, int baudRate, string owner, CancellationToken cancellationToken = default);

    /// <summary>释放一个参与者的引用；引用归零才真正关物理口（别人的会话不受影响）</summary>
    Task CloseAsync(string portName, string owner);

    /// <summary>
    /// 写字节：先同步广播 <paramref name="data"/> 给订阅者的 <c>onWritten</c>（日志 `↓` 帧入轴），
    /// 再走物理写。端口未打开 ⇒ <see cref="InvalidOperationException"/>。
    /// </summary>
    Task SendAsync(string portName, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>
    /// 订阅该口的数据流（多读者）。端口未打开 ⇒ <see cref="InvalidOperationException"/>。
    /// 返回的订阅随 <see cref="IDisposable.Dispose"/> 或端口关闭而失效。
    /// </summary>
    /// <param name="portName">物理口名</param>
    /// <param name="onReceived">收到字节（设备 → PC，读线程上触发）</param>
    /// <param name="onWritten">PC 写出字节（写前同步触发；日志旁路用它看 `↓` 帧）</param>
    IDisposable Subscribe(string portName,
        Action<ReadOnlyMemory<byte>> onReceived,
        Action<ReadOnlyMemory<byte>>? onWritten = null);
}
