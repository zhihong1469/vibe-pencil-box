using HidSharp;
using HidSharp.Reports;
using Microsoft.Extensions.Logging;
using PencilBox.Core.Communication;
using PencilBox.Core.Protocol;

namespace PencilBox.Infrastructure.Hid;

/// <summary>
/// 基于 HidSharp 的真正 USB HID 传输实现
/// </summary>
public class HidSharpTransport : IHidTransport
{
    private readonly ILogger<HidSharpTransport> _logger;
    private HidDevice? _device;
    private HidStream? _stream;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private bool _isConnected;
    private bool _readErrorReported;

    public bool IsConnected => _isConnected;
    public HidDeviceInfo? CurrentDevice { get; private set; }

    /// <summary>
    /// 当前设备是否为 custom_hid(EP4, 0xFF02) 无 Report ID 承载。
    /// 命中时写/读按纯帧处理（写以 0x00 虚拟 RID 占位、帧体紧随；读剥 0x00 还原纯帧）。
    /// </summary>
    public bool IsNoReportIdDevice { get; private set; }

    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
    public event EventHandler<HidReportReceivedEventArgs>? ReportReceived;

    public HidSharpTransport(ILogger<HidSharpTransport> logger)
    {
        _logger = logger;
    }

    public Task<IReadOnlyList<HidDeviceInfo>> EnumerateDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Enumerating HID devices...");

        var devices = new List<HidDeviceInfo>();

        try
        {
            var hidDevices = DeviceList.Local.GetHidDevices();

            foreach (var device in hidDevices)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    devices.Add(MapToHidDeviceInfo(device));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read HID device metadata");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate HID devices");
        }

        _logger.LogInformation("Found {Count} HID devices", devices.Count);
        return Task.FromResult<IReadOnlyList<HidDeviceInfo>>(devices);
    }

    public async Task<bool> ConnectAsync(string devicePath,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Connecting to HID device: {Path}", devicePath);

        try
        {
            await DisconnectAsync(cancellationToken);

            _device = DeviceList.Local.GetHidDevices()
                .FirstOrDefault(d => d.DevicePath.Equals(devicePath, StringComparison.OrdinalIgnoreCase));

            if (_device == null)
            {
                _logger.LogError("HID device not found: {Path}", devicePath);
                return false;
            }

            _stream = _device.Open();
            _isConnected = true;
            CurrentDevice = MapToHidDeviceInfo(_device);
            IsNoReportIdDevice = CurrentDevice?.UsagePage == ProtocolConstants.UsagePageCustomHid;

            StartReading();

            ConnectionStateChanged?.Invoke(this,
                new ConnectionStateChangedEventArgs(true, CurrentDevice));

            _logger.LogInformation("Connected to HID device: {Device}", CurrentDevice?.ProductName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to HID device: {Path}", devicePath);
            ConnectionStateChanged?.Invoke(this,
                new ConnectionStateChangedEventArgs(false, null, ex));
            return false;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Disconnecting from HID device...");

        StopReading();

        try
        {
            _stream?.Dispose();
            _stream = null;
            _device = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing HID stream");
        }

        _isConnected = false;
        var device = CurrentDevice;
        CurrentDevice = null;

        ConnectionStateChanged?.Invoke(this,
            new ConnectionStateChangedEventArgs(false, device));

        _logger.LogInformation("Disconnected from HID device");
        await Task.CompletedTask;
    }

    public async Task SendReportAsync(byte reportId, byte[] data,
        CancellationToken cancellationToken = default)
    {
        if (!_isConnected || _stream == null)
            throw new InvalidOperationException("Not connected to any HID device");

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // 无 Report ID 设备（custom_hid EP4）：写 `[0x00 占位报告ID][帧体]`。
            // 实测：Windows/HidSharp 会把 buffer 首字节当作报告 ID 剥掉；若直写帧体，0x4A 被当成报告 ID 剥除，
            // 设备收到 0x4C 开头 → 帧头丢失（Dongle 探针日志 head=4c00 证实）。故补 0x00 占位，使设备收到 4A 4C。
            if (IsNoReportIdDevice)
            {
                var frame = new byte[data.Length + 1];
                Array.Copy(data, 0, frame, 1, data.Length);

                await _stream.WriteAsync(frame, 0, frame.Length, cancellationToken);

                _logger.LogDebug("Sent EP4 no-RID frame [0x00+frame], length {Length}", frame.Length);
                return;
            }

            // HidSharp 要求 buffer 长度等于报告长度
            int reportLength = _device?.MaxOutputReportLength ?? 64;
            if (reportLength <= 0)
                reportLength = data.Length + 1;

            var report = new byte[reportLength];
            report[0] = reportId;
            Array.Copy(data, 0, report, 1, Math.Min(data.Length, reportLength - 1));

            await _stream.WriteAsync(report, 0, report.Length, cancellationToken);

            _logger.LogDebug("Sent HID Output report ID {ReportId}, length {Length}", reportId, report.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending HID report ID {ReportId}", reportId);
            throw;
        }
    }

    public async Task SendFeatureReportAsync(byte reportId, byte[] data,
        CancellationToken cancellationToken = default)
    {
        if (!_isConnected || _stream == null)
            throw new InvalidOperationException("Not connected to any HID device");

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            int featureLength = _device?.MaxFeatureReportLength ?? 64;
            if (featureLength <= 0)
                featureLength = data.Length + 1;

            var report = new byte[featureLength];
            report[0] = reportId;
            Array.Copy(data, 0, report, 1, Math.Min(data.Length, featureLength - 1));

            _stream.SetFeature(report, 0, report.Length);

            _logger.LogDebug("Sent HID Feature report ID {ReportId}, length {Length}", reportId, report.Length);
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending HID feature report ID {ReportId}", reportId);
            throw;
        }
    }

    private static HidDeviceInfo MapToHidDeviceInfo(HidDevice device)
    {
        var isOurDevice = ProtocolConstants.IsTargetVidPid((ushort)device.VendorID, (ushort)device.ProductID);
        var isCommandChannel = HasVendorCustomHid(device);   // 0xFF02 EP4 命令通道（唯一，标 ★）
        var hasLegacyChannel = HasCommandChannel(device);    // 0xA0/0x05 旧承载（备用识别）

        return new HidDeviceInfo
        {
            DevicePath = device.DevicePath,
            VendorId = (ushort)device.VendorID,
            ProductId = (ushort)device.ProductID,
            ProductName = SafeString(device.GetProductName, "Unknown HID Device"),
            Manufacturer = SafeString(device.GetManufacturer, string.Empty),
            SerialNumber = SafeString(device.GetSerialNumber, string.Empty),
            InputReportLength = device.MaxInputReportLength,
            OutputReportLength = device.MaxOutputReportLength,
            FeatureReportLength = device.MaxFeatureReportLength,
            IsHidDevice = true,
            // 仅 0xFF02 EP4 命令通道回填 Usage Page；旧 0xA0/0x05 集合不填（已废弃）
            UsagePage = isCommandChannel ? ProtocolConstants.UsagePageCustomHid : (ushort)0,
            IsOurDevice = isOurDevice,
            IsCommandChannel = isCommandChannel,
            IsTargetDongle = isOurDevice && (isCommandChannel || hasLegacyChannel)
        };
    }

    /// <summary>
    /// 逐项容错读取字符串描述：单个设备接口未授权读描述符时不应中断整次枚举/映射。
    /// </summary>
    private static string SafeString(Func<string?> getter, string fallback)
    {
        try { return getter() ?? fallback; }
        catch { return fallback; }
    }

    /// <summary>
    /// 检测报告描述符是否含 custom_hid 命令通道顶层集合（Usage Page 0xFF02，无 Report ID）。
    /// 唯一性来源：标准键鼠/音频设备不含此厂商集合。
    /// </summary>
    private static bool HasVendorCustomHid(HidDevice device)
    {
        try
        {
            var reportDesc = device.GetReportDescriptor();
            if (reportDesc == null)
                return false;

            foreach (var deviceItem in reportDesc.DeviceItems)
            {
                // Usages 为 32 位扩展 usage（高 16 位 = Usage Page，低 16 位 = Usage）
                foreach (var usage in deviceItem.Usages.GetAllValues())
                {
                    if (((uint)usage >> 16) == ProtocolConstants.UsagePageCustomHid)
                        return true;
                }
            }
            return false;
        }
        catch
        {
            // 某些设备不允许读取报告描述符，忽略
            return false;
        }
    }

    /// <summary>
    /// 检测报告描述符是否含厂商集合（Report ID 0xA0 OUT + 0x05 IN）。
    /// 这是识别目标 Dongle 的可靠特征：标准键鼠/触控板不含此厂商集合。
    /// </summary>
    private static bool HasCommandChannel(HidDevice device)
    {
        try
        {
            // 报告描述符解析出各 Report，检查是否有厂商集合 Report ID 0xA0 / 0x05
            var reportDesc = device.GetReportDescriptor();
            if (reportDesc == null)
                return false;

            bool hasDown = false, hasUp = false;
            foreach (var report in reportDesc.Reports)
            {
                if (report.ReportID == ProtocolConstants.ReportIdCommandDown) hasDown = true;  // 0xA0
                if (report.ReportID == ProtocolConstants.ReportIdCommandUp) hasUp = true;      // 0x05
            }
            return hasDown && hasUp;
        }
        catch
        {
            // 某些设备不允许读取报告描述符，忽略
            return false;
        }
    }

    private void StartReading()
    {
        _readCts = new CancellationTokenSource();
        _readTask = Task.Run(() => ReadLoop(_readCts.Token));
        _logger.LogDebug("HID read loop started");
    }

    private void StopReading()
    {
        _readCts?.Cancel();

        try
        {
            _readTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // ignore
        }

        _readCts?.Dispose();
        _readCts = null;
        _readTask = null;
        _logger.LogDebug("HID read loop stopped");
    }

    private async Task ReadLoop(CancellationToken cancellationToken)
    {
        if (_stream == null || _device == null)
            return;

        int reportLength = _device.MaxInputReportLength;
        if (reportLength <= 0)
            reportLength = 64;

        var buffer = new byte[reportLength];

        while (!cancellationToken.IsCancellationRequested && _isConnected)
        {
            try
            {
                int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);

                if (bytesRead > 0)
                {
                    byte reportId;
                    byte[] data;

                    if (IsNoReportIdDevice)
                    {
                        // EP4 无 RID 纯帧：剥掉前导 0x00（虚拟报告 ID 占位），还原纯帧；
                        // 若首字节非 0（设备只回 64B 纯帧、无占位），则不剥。
                        int offset = (bytesRead > 0 && buffer[0] == 0x00) ? 1 : 0;
                        reportId = 0;
                        data = new byte[bytesRead - offset];
                        if (data.Length > 0)
                            Array.Copy(buffer, offset, data, 0, data.Length);
                    }
                    else
                    {
                        reportId = buffer[0];
                        int dataLength = Math.Max(0, bytesRead - 1);
                        data = new byte[dataLength];
                        if (dataLength > 0)
                            Array.Copy(buffer, 1, data, 0, dataLength);
                    }

                    _logger.LogDebug("Received HID report ID {ReportId}, length {Length}", reportId, bytesRead);
                    _readErrorReported = false;   // 恢复正常读取，允许下次错误突发重新提示
                    ReportReceived?.Invoke(this, new HidReportReceivedEventArgs(reportId, data));
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 设备忙（如 BLE 扫描）或拔出时的读失败属瞬态，按"每次错误突发只提示一次"降噪
                if (!_readErrorReported)
                {
                    _readErrorReported = true;
                    _logger.LogWarning("HID 读取异常（后续同类错误降为 Debug）：{Message}", ex.Message);
                }
                else
                {
                    _logger.LogDebug(ex, "Error reading HID report");
                }

                await Task.Delay(10, cancellationToken);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
