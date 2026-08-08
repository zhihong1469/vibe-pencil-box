using Microsoft.Extensions.Logging;
using PencilBox.Core.Communication;
using PencilBox.Core.Models;
using PencilBox.Core.Protocol;
using PencilBox.Core.Services;

namespace PencilBox.Infrastructure.Services;

/// <summary>
/// 设备管理服务实现
/// </summary>
public class DeviceService : IDeviceService
{
    private readonly ILogger<DeviceService> _logger;
    private readonly IHidTransport _transport;
    private readonly PencilBoxProtocol _protocol;

    private DeviceInfo? _currentDeviceInfo;
    private IReadOnlyList<HidDeviceInfo> _devices = new List<HidDeviceInfo>();
    private IReadOnlyList<ConnectedDevice> _connectedDevices = new List<ConnectedDevice>();

    public bool IsConnected => _transport.IsConnected;
    public DeviceInfo? CurrentDeviceInfo
    {
        get => _currentDeviceInfo;
        private set
        {
            _currentDeviceInfo = value;
            DeviceInfoChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<IReadOnlyList<HidDeviceInfo>>? DeviceListChanged;
    public event EventHandler<DeviceInfo?>? DeviceInfoChanged;
    public event EventHandler<bool>? ConnectionStateChanged;

    public DeviceService(ILogger<DeviceService> logger, IHidTransport transport)
    {
        _logger = logger;
        _transport = transport;
        _protocol = new PencilBoxProtocol();

        _transport.ConnectionStateChanged += OnConnectionStateChanged;
        _transport.ReportReceived += OnReportReceived;
    }

    public async Task<IReadOnlyList<HidDeviceInfo>> ScanDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Scanning for HID devices...");

        _devices = await _transport.EnumerateDevicesAsync(cancellationToken);
        DeviceListChanged?.Invoke(this, _devices);

        _logger.LogInformation("Found {Count} HID devices", _devices.Count);
        return _devices;
    }

    public async Task<bool> ConnectAsync(string devicePath,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Connecting to device: {Path}", devicePath);

        var result = await _transport.ConnectAsync(devicePath, cancellationToken);

        if (result)
        {
            _logger.LogInformation("Connected successfully");
            await RefreshDeviceInfoAsync(cancellationToken);
        }
        else
        {
            _logger.LogError("Failed to connect");
        }

        return result;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Disconnecting...");
        await _transport.DisconnectAsync(cancellationToken);
        CurrentDeviceInfo = null;
        _connectedDevices = new List<ConnectedDevice>();
    }

    public async Task<DeviceInfo?> GetDeviceInfoAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_transport.IsConnected)
        {
            _logger.LogWarning("Not connected, cannot get device info");
            return null;
        }

        try
        {
            var command = PencilBoxProtocol.BuildGetDongleMessage();
            await _transport.SendReportAsync(ReportIds.CustomCommand, command, cancellationToken);
            _logger.LogDebug("Sent GetDongleMessage command");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send GetDongleMessage command");
        }

        return _currentDeviceInfo;
    }

    public async Task<IReadOnlyList<ConnectedDevice>> GetConnectedDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_transport.IsConnected)
        {
            _logger.LogWarning("Not connected, cannot get device list");
            return Array.Empty<ConnectedDevice>();
        }

        try
        {
            var command = PencilBoxProtocol.BuildGetConnectDevice();
            await _transport.SendReportAsync(ReportIds.CustomCommand, command, cancellationToken);
            _logger.LogDebug("Sent GetConnectDevice command");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send GetConnectDevice command");
        }

        return _connectedDevices;
    }

    public async Task<bool> DisconnectBluetoothDeviceAsync(byte deviceIndex,
        CancellationToken cancellationToken = default)
    {
        if (!_transport.IsConnected)
            return false;

        try
        {
            var command = PencilBoxProtocol.BuildDisconnectDevice(deviceIndex);
            await _transport.SendReportAsync(ReportIds.CustomCommand, command, cancellationToken);
            _logger.LogInformation("Sent disconnect command for device {Index}", deviceIndex);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send disconnect command");
            return false;
        }
    }

    public async Task<bool> ReconnectBluetoothDeviceAsync(byte deviceIndex,
        CancellationToken cancellationToken = default)
    {
        if (!_transport.IsConnected)
            return false;

        try
        {
            var command = PencilBoxProtocol.BuildReconnectDevice(deviceIndex);
            await _transport.SendReportAsync(ReportIds.CustomCommand, command, cancellationToken);
            _logger.LogInformation("Sent reconnect command for device {Index}", deviceIndex);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send reconnect command");
            return false;
        }
    }

    private async Task RefreshDeviceInfoAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(100, cancellationToken);
        await GetDeviceInfoAsync(cancellationToken);
    }

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        ConnectionStateChanged?.Invoke(this, e.IsConnected);

        if (!e.IsConnected)
        {
            CurrentDeviceInfo = null;
            _connectedDevices = new List<ConnectedDevice>();
        }
    }

    private void OnReportReceived(object? sender, HidReportReceivedEventArgs e)
    {
        try
        {
            var frame = PencilBoxProtocol.ParseResponse(e.Data);
            if (frame == null)
            {
                _logger.LogTrace("Failed to parse frame on report ID {ReportId}", e.ReportId);
                return;
            }

            _logger.LogDebug("Received frame: ReportId={ReportId}, Channel={Channel}, Cmd={Cmd}",
                e.ReportId, frame.Channel, frame.CommandId);

            switch (frame.CommandId)
            {
                case CommandId.GetDongleMessage:
                    ParseDeviceInfoResponse(frame);
                    break;

                case CommandId.GetConnectDevice:
                    ParseConnectedDevicesResponse(frame);
                    break;

                case CommandId.ReturnSucc:
                    _logger.LogDebug("Command succeeded");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing received report");
        }
    }

    private void ParseDeviceInfoResponse(DecodedFrame frame)
    {
        if (frame.Payload.Length < 8)
        {
            _logger.LogWarning("Device info response too short: {Length}", frame.Payload.Length);
            return;
        }

        var info = new DeviceInfo
        {
            DeviceType = frame.GetByte(0),
            DeviceId = frame.GetUInt16(1),
            Manufacturer = new string(new[]
            {
                (char)frame.GetByte(3),
                (char)frame.GetByte(4),
                (char)frame.GetByte(5),
                (char)frame.GetByte(6)
            }).TrimEnd('\0'),
            Version = frame.GetByte(7)
        };

        CurrentDeviceInfo = info;
        _logger.LogInformation("Device info: Type={Type}, ID={Id}, Manufacturer={Mfr}, Version={Ver}",
            info.DeviceType, info.DeviceId, info.Manufacturer, info.Version);
    }

    private void ParseConnectedDevicesResponse(DecodedFrame frame)
    {
        var devices = new List<ConnectedDevice>();

        for (int i = 0; i < frame.Payload.Length; i += 7)
        {
            if (i + 7 > frame.Payload.Length)
                break;

            devices.Add(new ConnectedDevice
            {
                Index = frame.GetByte(i),
                MacAddress = string.Format("{0:X2}:{1:X2}:{2:X2}:{3:X2}:{4:X2}:{5:X2}",
                    frame.GetByte(i + 1), frame.GetByte(i + 2), frame.GetByte(i + 3),
                    frame.GetByte(i + 4), frame.GetByte(i + 5), frame.GetByte(i + 6)),
                State = ConnectionState.Connected
            });
        }

        _connectedDevices = devices;
        _logger.LogInformation("Connected devices: {Count}", devices.Count);
    }
}
