using HidSharp;
using Microsoft.Extensions.Logging;
using PencilBox.Core.Communication;

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

    public bool IsConnected => _isConnected;
    public HidDeviceInfo? CurrentDevice { get; private set; }

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

            StartReading();

            ConnectionStateChanged?.Invoke(this,
                new ConnectionStateChangedEventArgs(true, CurrentDevice));

            _logger.LogInformation("Connected to HID device: {Device}", CurrentDevice.ProductName);
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
        return new HidDeviceInfo
        {
            DevicePath = device.DevicePath,
            VendorId = (ushort)device.VendorID,
            ProductId = (ushort)device.ProductID,
            ProductName = device.GetProductName() ?? "Unknown HID Device",
            Manufacturer = device.GetManufacturer() ?? string.Empty,
            SerialNumber = device.GetSerialNumber() ?? string.Empty,
            InputReportLength = device.MaxInputReportLength,
            OutputReportLength = device.MaxOutputReportLength,
            FeatureReportLength = device.MaxFeatureReportLength,
            IsHidDevice = true
        };
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
                    byte reportId = buffer[0];
                    int dataLength = Math.Max(0, bytesRead - 1);
                    byte[] data = new byte[dataLength];
                    if (dataLength > 0)
                        Array.Copy(buffer, 1, data, 0, dataLength);

                    _logger.LogDebug("Received HID report ID {ReportId}, length {Length}", reportId, bytesRead);
                    ReportReceived?.Invoke(this, new HidReportReceivedEventArgs(reportId, data));
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading HID report");
                await Task.Delay(10, cancellationToken);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
