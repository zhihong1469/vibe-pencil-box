namespace PencilBox.Core.Communication;

/// <summary>
/// HID 传输接口
/// </summary>
public interface IHidTransport : IAsyncDisposable
{
    /// <summary>
    /// 设备连接状态变化事件
    /// </summary>
    event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    /// <summary>
    /// HID 报告数据接收事件
    /// </summary>
    event EventHandler<HidReportReceivedEventArgs>? ReportReceived;

    /// <summary>
    /// 枚举所有已连接的 HID 设备
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>设备信息列表</returns>
    Task<IReadOnlyList<HidDeviceInfo>> EnumerateDevicesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 连接到指定设备
    /// </summary>
    /// <param name="devicePath">设备路径</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>是否连接成功</returns>
    Task<bool> ConnectAsync(string devicePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 断开当前连接的设备
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 发送 HID 报告（通过 Output Report 或 Feature Report）
    /// </summary>
    /// <param name="reportId">报告 ID</param>
    /// <param name="data">报告数据（不含 Report ID 字节）</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task SendReportAsync(byte reportId, byte[] data,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 发送 Feature Report（控制传输，对应 MyUSB help 中的 control transfer）
    /// </summary>
    Task SendFeatureReportAsync(byte reportId, byte[] data,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 当前是否已连接
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// 当前连接的设备信息
    /// </summary>
    HidDeviceInfo? CurrentDevice { get; }
}

/// <summary>
/// 设备连接状态变化事件参数
/// </summary>
public class ConnectionStateChangedEventArgs : EventArgs
{
    /// <summary>
    /// 是否已连接
    /// </summary>
    public bool IsConnected { get; }

    /// <summary>
    /// 设备信息（断开时可能为 null）
    /// </summary>
    public HidDeviceInfo? Device { get; }

    /// <summary>
    /// 异常信息（连接失败时）
    /// </summary>
    public Exception? Exception { get; }

    public ConnectionStateChangedEventArgs(bool isConnected, HidDeviceInfo? device = null, Exception? exception = null)
    {
        IsConnected = isConnected;
        Device = device;
        Exception = exception;
    }
}

/// <summary>
/// HID 报告接收事件参数
/// </summary>
public class HidReportReceivedEventArgs : EventArgs
{
    /// <summary>
    /// Report ID
    /// </summary>
    public byte ReportId { get; }

    /// <summary>
    /// 报告数据（不含 Report ID 字节）
    /// </summary>
    public byte[] Data { get; }

    /// <summary>
    /// 时间戳
    /// </summary>
    public DateTime Timestamp { get; }

    /// <summary>
    /// 报告类型
    /// </summary>
    public HidReportType ReportType { get; }

    public HidReportReceivedEventArgs(byte reportId, byte[] data, HidReportType reportType = HidReportType.Input)
    {
        ReportId = reportId;
        Data = data;
        Timestamp = DateTime.Now;
        ReportType = reportType;
    }
}

/// <summary>
/// HID 报告类型
/// </summary>
public enum HidReportType
{
    /// <summary>
    /// Input Report（来自设备）
    /// </summary>
    Input,

    /// <summary>
    /// Output Report（发往设备）
    /// </summary>
    Output,

    /// <summary>
    /// Feature Report（双向）
    /// </summary>
    Feature
}
