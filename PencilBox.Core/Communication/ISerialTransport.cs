namespace PencilBox.Core.Communication;

/// <summary>
/// 串口传输接口（标准串口助手功能）
/// </summary>
public interface ISerialTransport : IAsyncDisposable
{
    /// <summary>
    /// 连接状态变化事件
    /// </summary>
    event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    /// <summary>
    /// 串口数据接收事件
    /// </summary>
    event EventHandler<SerialDataReceivedEventArgs>? DataReceived;

    /// <summary>
    /// 枚举可用串口
    /// </summary>
    Task<IReadOnlyList<SerialPortInfo>> EnumeratePortsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 打开串口
    /// </summary>
    /// <param name="portName">串口名称（如 COM3）</param>
    /// <param name="settings">串口配置</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task<bool> ConnectAsync(string portName, SerialPortSettings settings,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 关闭串口
    /// </summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 发送原始字节数据
    /// </summary>
    Task SendAsync(byte[] data, CancellationToken cancellationToken = default);

    /// <summary>
    /// 发送文本（按当前编码转换为字节）
    /// </summary>
    Task SendTextAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// 发送十六进制字符串（如 "01 02 03 FF"）
    /// </summary>
    Task SendHexAsync(string hexString, CancellationToken cancellationToken = default);

    /// <summary>
    /// 当前是否已连接
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// 当前打开的串口名称
    /// </summary>
    string? CurrentPortName { get; }

    /// <summary>
    /// 当前串口配置
    /// </summary>
    SerialPortSettings? CurrentSettings { get; }
}
