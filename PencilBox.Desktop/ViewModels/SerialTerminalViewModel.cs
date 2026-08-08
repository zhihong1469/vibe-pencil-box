using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PencilBox.Core.Communication;
using PencilBox.Desktop.Helpers;

namespace PencilBox.Desktop.ViewModels;

public partial class SerialTerminalViewModel : ObservableObject
{
    private readonly ISerialTransport _serialTransport;
    private readonly StringBuilder _receiveBuffer = new();

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private SerialPortInfo? _selectedPort;

    [ObservableProperty]
    private int _baudRate = 115200;

    [ObservableProperty]
    private int _dataBits = 8;

    [ObservableProperty]
    private int _selectedStopBitsIndex = 1; // One

    [ObservableProperty]
    private int _selectedParityIndex = 0; // None

    [ObservableProperty]
    private string _sendText = string.Empty;

    [ObservableProperty]
    private bool _isHexMode;

    [ObservableProperty]
    private bool _showTimestamp = true;

    [ObservableProperty]
    private string _receivedText = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    public ObservableCollection<SerialPortInfo> AvailablePorts { get; } = new();

    public List<int> BaudRates { get; } = new()
    {
        9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600
    };

    public List<int> DataBitsList { get; } = new() { 5, 6, 7, 8 };

    public List<string> StopBitsList { get; } = new()
    {
        "None", "One", "OnePointFive", "Two"
    };

    public List<string> ParityList { get; } = new()
    {
        "None", "Odd", "Even", "Mark", "Space"
    };

    public SerialTerminalViewModel(ISerialTransport serialTransport)
    {
        _serialTransport = serialTransport;
        _serialTransport.ConnectionStateChanged += OnConnectionStateChanged;
        _serialTransport.DataReceived += OnDataReceived;

        _ = RefreshPortsAsync();
    }

    [RelayCommand]
    private async Task RefreshPortsAsync()
    {
        try
        {
            StatusMessage = "Refreshing ports...";
            var ports = await _serialTransport.EnumeratePortsAsync();

            MainThreadHelper.EnsureOnMainThread(() =>
            {
                AvailablePorts.Clear();
                foreach (var port in ports)
                {
                    AvailablePorts.Add(port);
                }

                SelectedPort ??= AvailablePorts.FirstOrDefault();
                StatusMessage = $"Found {ports.Count} port(s)";
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (SelectedPort == null)
        {
            StatusMessage = "Please select a port";
            return;
        }

        var settings = new SerialPortSettings
        {
            BaudRate = BaudRate,
            DataBits = DataBits,
            StopBits = (SerialStopBits)SelectedStopBitsIndex,
            Parity = (SerialParity)SelectedParityIndex,
            ReadTimeout = 100,
            WriteTimeout = 100
        };

        StatusMessage = $"Opening {SelectedPort.PortName}...";
        var result = await _serialTransport.ConnectAsync(SelectedPort.PortName, settings);
        StatusMessage = result
            ? $"Connected to {SelectedPort.PortName}"
            : $"Failed to open {SelectedPort.PortName}";
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        await _serialTransport.DisconnectAsync();
        StatusMessage = "Disconnected";
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(SendText))
            return;

        try
        {
            if (IsHexMode)
            {
                await _serialTransport.SendHexAsync(SendText);
                AppendSentLog($"[HEX] {SendText.Trim()}");
            }
            else
            {
                await _serialTransport.SendTextAsync(SendText);
                AppendSentLog($"[TXT] {SendText}");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Send error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ClearReceived()
    {
        _receiveBuffer.Clear();
        ReceivedText = string.Empty;
    }

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        MainThreadHelper.EnsureOnMainThread(() =>
        {
            IsConnected = e.IsConnected;
            StatusMessage = e.IsConnected
                ? $"Connected to {e.Device?.DevicePath}"
                : $"Disconnected{(e.Exception != null ? $": {e.Exception.Message}" : string.Empty)}";
        });
    }

    private void OnDataReceived(object? sender, PencilBox.Core.Communication.SerialDataReceivedEventArgs e)
    {
        var hex = BitConverter.ToString(e.Data).Replace("-", " ");
        var text = Encoding.UTF8.GetString(e.Data).Replace("\0", " ");
        var timestamp = ShowTimestamp ? $"[{e.Timestamp:HH:mm:ss.fff}] " : string.Empty;
        var line = $"{timestamp}RX ({e.Length} bytes): {hex} | '{text}'{Environment.NewLine}";

        _receiveBuffer.Append(line);

        MainThreadHelper.EnsureOnMainThread(() =>
        {
            ReceivedText = _receiveBuffer.ToString();
        });
    }

    private void AppendSentLog(string message)
    {
        var timestamp = ShowTimestamp ? $"[{DateTime.Now:HH:mm:ss.fff}] " : string.Empty;
        _receiveBuffer.Append($"{timestamp}TX: {message}{Environment.NewLine}");

        MainThreadHelper.EnsureOnMainThread(() =>
        {
            ReceivedText = _receiveBuffer.ToString();
        });
    }
}
