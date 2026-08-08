using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PencilBox.Core.Communication;
using PencilBox.Core.Models;
using PencilBox.Core.Protocol;
using PencilBox.Core.Services;
using PencilBox.Desktop.Helpers;

namespace PencilBox.Desktop.ViewModels;

public partial class DevicePageViewModel : ObservableObject
{
    private readonly IDeviceService _deviceService;
    private readonly IHidTransport _transport;

    /// <summary>
    /// 通信日志
    /// </summary>
    public ObservableCollection<string> LogEntries { get; } = new();

    [ObservableProperty]
    private ObservableCollection<HidDeviceInfo> _availableDevices = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    private HidDeviceInfo? _selectedDevice;

    [ObservableProperty]
    private ObservableCollection<ConnectedDevice> _connectedDevices = new();

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    private bool _isConnecting;

    [ObservableProperty]
    private string _statusMessage = "Click Scan to find USB HID devices";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    private bool _isConnected;

    [ObservableProperty]
    private DeviceInfo? _deviceInfo;

    // ========== 发送配置 ==========

    [ObservableProperty]
    private byte _selectedSendReportId = 0xA0;

    [ObservableProperty]
    private string _sendText = string.Empty;

    [ObservableProperty]
    private bool _isHexSend = true;

    [ObservableProperty]
    private bool _isHexReceive = true;

    [ObservableProperty]
    private int _sendIntervalMs = 5;

    [ObservableProperty]
    private bool _isLoopSending;

    private CancellationTokenSource? _loopSendCts;

    // ========== Feature Report (控制传输) ==========

    [ObservableProperty]
    private string _featureRequestType = "80";

    [ObservableProperty]
    private string _featureRequest = "06";

    [ObservableProperty]
    private string _featureValue = "0100";

    [ObservableProperty]
    private string _featureIndex = "0000";

    [ObservableProperty]
    private string _featureLength = "0012";

    // ========== 设备端点信息 ==========

    [ObservableProperty]
    private string _endpointInfo = "No device selected";

    public bool CanConnect => SelectedDevice != null && !IsConnected && !IsConnecting;

    public List<byte> AvailableReportIds { get; } = new()
    {
        0x01, 0x02, 0x03, 0x04, 0x05, 0xA0
    };

    public DevicePageViewModel(IDeviceService deviceService, IHidTransport transport)
    {
        _deviceService = deviceService;
        _transport = transport;
        _deviceService.DeviceListChanged += OnDeviceListChanged;
        _deviceService.ConnectionStateChanged += OnConnectionStateChanged;
        _deviceService.DeviceInfoChanged += OnDeviceInfoChanged;
        _transport.ReportReceived += OnHidReportReceived;
    }

    private void OnDeviceListChanged(object? sender, IReadOnlyList<HidDeviceInfo> devices)
    {
        MainThreadHelper.EnsureOnMainThread(() =>
        {
            AvailableDevices = new ObservableCollection<HidDeviceInfo>(devices);
            IsScanning = false;
            StatusMessage = $"Found {devices.Count} HID device(s)";
            AddLog($"[SCAN] Found {devices.Count} HID device(s)");
        });
    }

    private void OnConnectionStateChanged(object? sender, bool connected)
    {
        MainThreadHelper.EnsureOnMainThread(() =>
        {
            IsConnected = connected;
            IsConnecting = false;
            StatusMessage = connected ? "Successfully connected" : "Disconnected";
            AddLog(connected ? "[CONN] Device connected" : "[CONN] Device disconnected");
        });
    }

    private void OnDeviceInfoChanged(object? sender, DeviceInfo? info)
    {
        MainThreadHelper.EnsureOnMainThread(() =>
        {
            DeviceInfo = info;
            if (info != null)
            {
                StatusMessage = $"Device: {info.Manufacturer} v{info.Version}";
                AddLog($"[INFO] Type={info.DeviceType:X2} ID={info.DeviceId:X4} Mfr='{info.Manufacturer}' Ver={info.Version}");
            }
        });
    }

    private void OnHidReportReceived(object? sender, HidReportReceivedEventArgs e)
    {
        MainThreadHelper.EnsureOnMainThread(() =>
        {
            var decoded = HidReportDecoder.DecodeAuto(e.ReportId, e.Data);
            var line = $"[RX] {e.Timestamp:HH:mm:ss.fff} RID=0x{e.ReportId:X2} ({HidReportDecoder.GetReportDescription(e.ReportId)}) | {decoded}";

            if (IsHexReceive)
            {
                line += $" | HEX: {HidReportDecoder.BytesToHex(e.Data)}";
            }

            AddLog(line);
        });
    }

    private void AddLog(string entry)
    {
        // 限制日志最多 1000 条，防止内存爆炸
        if (LogEntries.Count > 1000)
        {
            for (int i = 0; i < 100; i++)
                LogEntries.RemoveAt(0);
        }
        LogEntries.Add(entry);
    }

    [RelayCommand]
    private async Task ScanDevicesAsync()
    {
        try
        {
            IsScanning = true;
            StatusMessage = "Scanning for devices...";
            AddLog("[SCAN] Searching for HID devices...");

            var devices = await _deviceService.ScanDevicesAsync();

            MainThreadHelper.EnsureOnMainThread(() =>
            {
                AvailableDevices = new ObservableCollection<HidDeviceInfo>(devices);
                IsScanning = false;
                StatusMessage = devices.Count > 0
                    ? $"Found {devices.Count} device(s)"
                    : "No HID devices found";
                AddLog($"[SCAN] Done. Found {devices.Count} device(s)");
            });
        }
        catch (Exception ex)
        {
            MainThreadHelper.EnsureOnMainThread(() =>
            {
                IsScanning = false;
                StatusMessage = $"Error: {ex.Message}";
                AddLog($"[ERR ] Scan failed: {ex.Message}");
            });
        }
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (SelectedDevice == null) return;

        try
        {
            IsConnecting = true;
            StatusMessage = $"Connecting to {SelectedDevice.ProductName}...";
            AddLog($"[CONN] Connecting to {SelectedDevice}");

            var result = await _deviceService.ConnectAsync(SelectedDevice.DevicePath);

            MainThreadHelper.EnsureOnMainThread(() =>
            {
                IsConnecting = false;
                if (result)
                {
                    EndpointInfo = $"VID: 0x{SelectedDevice.VendorId:X4} | PID: 0x{SelectedDevice.ProductId:X4}\n" +
                                   $"Input  report: {SelectedDevice.InputReportLength} bytes\n" +
                                   $"Output report: {SelectedDevice.OutputReportLength} bytes\n" +
                                   $"Feature report: {SelectedDevice.FeatureReportLength} bytes";
                }
                else
                {
                    StatusMessage = "Connection failed";
                    AddLog("[ERR ] Connection failed");
                }
            });
        }
        catch (Exception ex)
        {
            MainThreadHelper.EnsureOnMainThread(() =>
            {
                IsConnecting = false;
                StatusMessage = $"Error: {ex.Message}";
                AddLog($"[ERR ] Connection error: {ex.Message}");
            });
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        try
        {
            await _deviceService.DisconnectAsync();
            MainThreadHelper.EnsureOnMainThread(() =>
            {
                StatusMessage = "Disconnected";
                EndpointInfo = "No device selected";
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RefreshDeviceInfoAsync()
    {
        try
        {
            StatusMessage = "Refreshing device info...";
            await _deviceService.GetDeviceInfoAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RefreshConnectedDevicesAsync()
    {
        try
        {
            var devices = await _deviceService.GetConnectedDevicesAsync();
            MainThreadHelper.EnsureOnMainThread(() =>
            {
                ConnectedDevices = new ObservableCollection<ConnectedDevice>(devices);
                StatusMessage = $"Connected BLE devices: {devices.Count}";
                AddLog($"[SCAN] Connected BLE devices: {devices.Count}");
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SendRawReportAsync()
    {
        if (!IsConnected)
        {
            StatusMessage = "Not connected";
            return;
        }

        try
        {
            byte[] data = ParseSendData(SendText, IsHexSend);
            await _transport.SendReportAsync(SelectedSendReportId, data);

            var logData = IsHexSend ? HidReportDecoder.BytesToHex(data) : Encoding.UTF8.GetString(data);
            AddLog($"[TX ] {DateTime.Now:HH:mm:ss.fff} RID=0x{SelectedSendReportId:X2} ({HidReportDecoder.GetReportDescription(SelectedSendReportId)}) | {logData}");
            StatusMessage = $"Sent {data.Length} bytes to RID 0x{SelectedSendReportId:X2}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Send error: {ex.Message}";
            AddLog($"[ERR ] Send error: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ToggleLoopSend()
    {
        if (IsLoopSending)
        {
            StopLoopSend();
        }
        else
        {
            StartLoopSend();
        }
    }

    private void StartLoopSend()
    {
        if (!IsConnected)
        {
            StatusMessage = "Not connected";
            return;
        }

        _loopSendCts = new CancellationTokenSource();
        IsLoopSending = true;
        var token = _loopSendCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    byte[] data = ParseSendData(SendText, IsHexSend);
                    await _transport.SendReportAsync(SelectedSendReportId, data);

                    MainThreadHelper.EnsureOnMainThread(() =>
                    {
                        var logData = IsHexSend ? HidReportDecoder.BytesToHex(data) : Encoding.UTF8.GetString(data);
                        AddLog($"[TX*] {DateTime.Now:HH:mm:ss.fff} RID=0x{SelectedSendReportId:X2} | {logData}");
                    });

                    await Task.Delay(Math.Max(1, SendIntervalMs), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    MainThreadHelper.EnsureOnMainThread(() =>
                    {
                        AddLog($"[ERR ] Loop send: {ex.Message}");
                    });
                    break;
                }
            }

            MainThreadHelper.EnsureOnMainThread(() => IsLoopSending = false);
        });

        StatusMessage = $"Loop sending every {SendIntervalMs}ms";
    }

    private void StopLoopSend()
    {
        _loopSendCts?.Cancel();
        _loopSendCts?.Dispose();
        _loopSendCts = null;
        IsLoopSending = false;
        StatusMessage = "Loop sending stopped";
    }

    [RelayCommand]
    private async Task ExecuteFeatureReportAsync()
    {
        if (!IsConnected)
        {
            StatusMessage = "Not connected";
            return;
        }

        try
        {
            // 解析 Feature Report 参数
            byte requestType = ParseByte(FeatureRequestType, "RequestType");
            byte request = ParseByte(FeatureRequest, "Request");
            ushort value = ParseUShort(FeatureValue, "Value");
            ushort index = ParseUShort(FeatureIndex, "Index");
            ushort length = ParseUShort(FeatureLength, "Length");

            // 构造数据 buffer
            var data = new byte[Math.Max((int)length, 1)];

            AddLog($"[FEAT] Type=0x{requestType:X2} Req=0x{request:X2} Val=0x{value:X4} Idx=0x{index:X4} Len={length}");

            await _transport.SendFeatureReportAsync(0x00, data);

            StatusMessage = $"Feature Report executed (0x{requestType:X2}/0x{request:X2})";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Feature error: {ex.Message}";
            AddLog($"[ERR ] Feature error: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ClearLog()
    {
        LogEntries.Clear();
        StatusMessage = "Log cleared";
    }

    [RelayCommand]
    private async Task SaveLogAsync()
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                DefaultExt = "txt",
                FileName = $"pencilbox_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
                Title = "Save Communication Log"
            };

            if (dialog.ShowDialog() != true)
                return;

            var content = string.Join(Environment.NewLine, LogEntries);
            await System.IO.File.WriteAllTextAsync(dialog.FileName, content);
            StatusMessage = $"Log saved to {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save log error: {ex.Message}";
        }
    }

    private static byte[] ParseSendData(string text, bool isHex)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<byte>();

        if (isHex)
        {
            var cleaned = System.Text.RegularExpressions.Regex.Replace(text, @"[^0-9A-Fa-f]", "");
            if (cleaned.Length % 2 != 0)
                throw new ArgumentException("Hex string length must be even");

            var bytes = new byte[cleaned.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(cleaned.Substring(i * 2, 2), 16);
            }
            return bytes;
        }
        else
        {
            return Encoding.UTF8.GetBytes(text);
        }
    }

    private static byte ParseByte(string hex, string fieldName)
    {
        var cleaned = System.Text.RegularExpressions.Regex.Replace(hex ?? string.Empty, @"[^0-9A-Fa-f]", "");
        if (cleaned.Length == 0) return 0;
        if (byte.TryParse(cleaned, System.Globalization.NumberStyles.HexNumber, null, out byte val))
            return val;
        throw new ArgumentException($"{fieldName} is not a valid hex byte");
    }

    private static ushort ParseUShort(string hex, string fieldName)
    {
        var cleaned = System.Text.RegularExpressions.Regex.Replace(hex ?? string.Empty, @"[^0-9A-Fa-f]", "");
        if (cleaned.Length == 0) return 0;
        if (ushort.TryParse(cleaned, System.Globalization.NumberStyles.HexNumber, null, out ushort val))
            return val;
        throw new ArgumentException($"{fieldName} is not a valid hex short");
    }
}