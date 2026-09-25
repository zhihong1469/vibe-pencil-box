// <copyright file="DeviceService.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using Microsoft.Extensions.Logging;
using PencilBox.Core.Communication;
using PencilBox.Core.Models;
using PencilBox.Core.Protocol;
using PencilBox.Core.Services;

namespace PencilBox.Infrastructure.Services;

/// <summary>
/// 设备管理服务实现
/// 实现 IDeviceService + ICommandClient（事务模型），对齐 link/host/usb 系列协议。
/// </summary>
public class DeviceService : IDeviceService, ICommandClient
{
    private readonly ILogger<DeviceService> _logger;
    private readonly IHidTransport _transport;

    private DeviceInfo? _currentDeviceInfo;
    private IReadOnlyList<HidDeviceInfo> _devices = new List<HidDeviceInfo>();
    private IReadOnlyList<ConnectedDevice> _connectedDevices = new List<ConnectedDevice>();

    // 事务等待（单事务模型：PC 同一时刻只发一个 CUSTOM 请求）
    private readonly object _txLock = new();
    private TaskCompletionSource<byte[]>? _pendingTcs;
    private byte _pendingSubCmd;

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

        _transport.ConnectionStateChanged += OnConnectionStateChanged;
        _transport.ReportReceived += OnReportReceived;
    }

    // ==================== IDeviceService ====================

    public async Task<IReadOnlyList<HidDeviceInfo>> ScanDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Scanning for HID devices...");
        _devices = await _transport.EnumerateDevicesAsync(cancellationToken);
        DeviceListChanged?.Invoke(this, _devices);
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
            await GetDeviceInfoAsync(cancellationToken);
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
            return null;

        try
        {
            var command = PencilBoxProtocol.BuildGetDongleMessage();
            await _transport.SendReportAsync(ReportIds.CustomCommand, command, cancellationToken);
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
            return Array.Empty<ConnectedDevice>();

        try
        {
            var command = PencilBoxProtocol.BuildGetConnectDevice();
            await _transport.SendReportAsync(ReportIds.CustomCommand, command, cancellationToken);
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
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send reconnect command");
            return false;
        }
    }

    // ==================== ICommandClient（事务模型） ====================

    public async Task<byte[]?> SendCustomAsync(
        PcSubCommand subCmd,
        byte[]? data = null,
        int timeoutMs = 5000,
        CancellationToken ct = default)
    {
        if (!_transport.IsConnected)
            throw new InvalidOperationException("Not connected to Dongle");

        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_txLock)
        {
            if (_pendingTcs != null)
                throw new InvalidOperationException("Another command is already in flight");
            _pendingTcs = tcs;
            _pendingSubCmd = (byte)subCmd;
        }

        try
        {
            var command = PencilBoxProtocol.BuildCustomCommand((byte)subCmd, data);
            await _transport.SendReportAsync(ReportIds.CustomCommand, command, ct);
        }
        catch
        {
            lock (_txLock)
            {
                if (_pendingTcs == tcs) { _pendingTcs = null; _pendingSubCmd = 0; }
            }
            throw;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);

        try
        {
            return await tcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogWarning("Command 0x{SubCmd:X2} timed out after {Timeout}ms", (byte)subCmd, timeoutMs);
            return null;
        }
        finally
        {
            lock (_txLock)
            {
                if (_pendingTcs == tcs) { _pendingTcs = null; _pendingSubCmd = 0; }
            }
        }
    }

    // ==================== 事件处理 ====================

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        ConnectionStateChanged?.Invoke(this, e.IsConnected);

        if (!e.IsConnected)
        {
            CurrentDeviceInfo = null;
            _connectedDevices = new List<ConnectedDevice>();

            lock (_txLock)
            {
                _pendingTcs?.TrySetCanceled();
                _pendingTcs = null;
                _pendingSubCmd = 0;
            }
        }
    }

    private void OnReportReceived(object? sender, HidReportReceivedEventArgs e)
    {
        try
        {
            var frame = PencilBoxProtocol.ParseResponse(e.Data);
            if (frame == null)
                return;

            // CUSTOM 命令（应答 / 上行事件）
            if (frame.CommandId == CommandId.Custom)
            {
                HandleCustomFrame(frame);
                return;
            }

            switch (frame.CommandId)
            {
                case CommandId.GetDongleMessage:
                    ParseDeviceInfoResponse(frame);
                    break;

                case CommandId.GetConnectDevice:
                    ParseConnectedDevicesResponse(frame);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing received report");
        }
    }

    private void HandleCustomFrame(DecodedFrame frame)
    {
        if (frame.Payload.Length == 0)
            return;

        byte subCmd = frame.Payload[0];
        byte[] respData = frame.Payload.Length > 1
            ? frame.Payload.AsSpan(1).ToArray()
            : Array.Empty<byte>();

        // 匹配挂起的事务
        TaskCompletionSource<byte[]>? tcs = null;
        lock (_txLock)
        {
            if (_pendingSubCmd == subCmd)
            {
                tcs = _pendingTcs;
                _pendingTcs = null;
                _pendingSubCmd = 0;
            }
        }

        if (tcs != null)
        {
            tcs.TrySetResult(respData);
            return;
        }

        // 上行事件（0x50~0x5F，异步单向通知）
        _logger.LogDebug("Received custom event Sub-CMD 0x{SubCmd:X2}, {Len} bytes", subCmd, respData.Length);
    }

    private void ParseDeviceInfoResponse(DecodedFrame frame)
    {
        // 应答：payload = [型号(2B)][固件版本(4B)][协议版本(1B)][能力位(1B)]
        if (frame.Payload.Length < 8)
        {
            _logger.LogWarning("Device info response too short: {Length}", frame.Payload.Length);
            return;
        }

        var info = new DeviceInfo
        {
            Model = frame.GetUInt16(0),
            FirmwareVersion = ((uint)frame.GetByte(2) << 24) | ((uint)frame.GetByte(3) << 16)
                              | ((uint)frame.GetByte(4) << 8) | frame.GetByte(5),
            ProtocolVersion = frame.GetByte(6),
            Capabilities = frame.GetByte(7)
        };

        // 保留 HID 枚举信息（型号/VID/PID）
        if (_transport.CurrentDevice != null)
        {
            info.VendorId = _transport.CurrentDevice.VendorId;
            info.ProductId = _transport.CurrentDevice.ProductId;
            info.ProductName = _transport.CurrentDevice.ProductName;
        }

        CurrentDeviceInfo = info;
        _logger.LogInformation("Dongle info: Model=0x{Model:X4}, FW={Fw}, Proto={Proto}, Caps=0x{Caps:X2}",
            info.Model, info.FirmwareDisplay, info.ProtocolVersion, info.Capabilities);
    }

    private void ParseConnectedDevicesResponse(DecodedFrame frame)
    {
        // 应答：payload = [count][(channel)(state)×count]
        if (frame.Payload.Length < 1)
            return;

        int count = frame.GetByte(0);
        var devices = new List<ConnectedDevice>();

        for (int i = 0; i < count; i++)
        {
            int offset = 1 + i * 2;
            if (offset + 2 > frame.Payload.Length)
                break;

            byte channel = frame.GetByte(offset);
            byte state = frame.GetByte(offset + 1);

            devices.Add(new ConnectedDevice
            {
                Index = channel,
                State = state == 1 ? ConnectionState.Connected : ConnectionState.Disconnected
            });
        }

        _connectedDevices = devices;
        _logger.LogInformation("Connected devices: {Count}", devices.Count);
    }
}
