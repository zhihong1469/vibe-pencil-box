using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Management;
using System.Text;
using Microsoft.Extensions.Logging;
using PencilBox.Core.Communication;

namespace PencilBox.Infrastructure.Serial;

/// <summary>
/// 标准串口助手实现
/// </summary>
public class SerialTransport : ISerialTransport
{
    private readonly ILogger<SerialTransport> _logger;
    private SerialPort? _serialPort;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private bool _isConnected;

    public bool IsConnected => _isConnected;
    public string? CurrentPortName { get; private set; }
    public SerialPortSettings? CurrentSettings { get; private set; }

    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
    public event EventHandler<Core.Communication.SerialDataReceivedEventArgs>? DataReceived;

    public SerialTransport(ILogger<SerialTransport> logger)
    {
        _logger = logger;
    }

    public Task<IReadOnlyList<SerialPortInfo>> EnumeratePortsAsync(
        CancellationToken cancellationToken = default)
    {
        var ports = new List<SerialPortInfo>();

        try
        {
            var portNames = SerialPort.GetPortNames().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _logger.LogInformation("SerialPort.GetPortNames() returned {Count} ports", portNames.Count);

            // 通过 WMI 获取更友好的描述（可选，失败不阻塞）
            var descriptions = GetPortDescriptions();

            foreach (var name in portNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                descriptions.TryGetValue(name, out var description);

                ports.Add(new SerialPortInfo
                {
                    PortName = name,
                    Description = description ?? string.Empty
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate serial ports");
        }

        return Task.FromResult<IReadOnlyList<SerialPortInfo>>(ports);
    }

    public async Task<bool> ConnectAsync(string portName, SerialPortSettings settings,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Opening serial port {Port} @ {Baud}bps", portName, settings.BaudRate);

        try
        {
            await DisconnectAsync(cancellationToken);

            _serialPort = new SerialPort(portName, settings.BaudRate)
            {
                DataBits = settings.DataBits,
                StopBits = (StopBits)settings.StopBits,
                Parity = (Parity)settings.Parity,
                Handshake = (Handshake)settings.Handshake,
                ReadTimeout = settings.ReadTimeout,
                WriteTimeout = settings.WriteTimeout,
                Encoding = Encoding.UTF8,
                DtrEnable = true,
                RtsEnable = true
            };

            _serialPort.Open();
            _isConnected = true;
            CurrentPortName = portName;
            CurrentSettings = settings;

            StartReading();

            ConnectionStateChanged?.Invoke(this,
                new ConnectionStateChangedEventArgs(true, new HidDeviceInfo
                {
                    DevicePath = portName,
                    ProductName = $"Serial {portName}"
                }));

            _logger.LogInformation("Serial port {Port} opened", portName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open serial port {Port}", portName);
            ConnectionStateChanged?.Invoke(this,
                new ConnectionStateChangedEventArgs(false, null, ex));
            return false;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (!_isConnected && _serialPort == null)
            return;

        _logger.LogInformation("Closing serial port...");

        StopReading();

        try
        {
            if (_serialPort != null)
            {
                if (_serialPort.IsOpen)
                    _serialPort.Close();
                _serialPort.Dispose();
                _serialPort = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error closing serial port");
        }

        _isConnected = false;
        var portName = CurrentPortName;
        CurrentPortName = null;
        CurrentSettings = null;

        ConnectionStateChanged?.Invoke(this,
            new ConnectionStateChangedEventArgs(false, new HidDeviceInfo
            {
                DevicePath = portName ?? string.Empty,
                ProductName = $"Serial {portName}"
            }));

        _logger.LogInformation("Serial port {Port} closed", portName);
        await Task.CompletedTask;
    }

    public async Task SendAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        if (!_isConnected || _serialPort == null || !_serialPort.IsOpen)
            throw new InvalidOperationException("Serial port not connected");

        cancellationToken.ThrowIfCancellationRequested();

        await Task.Run(() =>
        {
            _serialPort.Write(data, 0, data.Length);
        }, cancellationToken);

        _logger.LogDebug("Sent {Bytes} bytes to {Port}", data.Length, CurrentPortName);
    }

    public async Task SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (text == null)
            return;

        var bytes = Encoding.UTF8.GetBytes(text);
        await SendAsync(bytes, cancellationToken);
    }

    public async Task SendHexAsync(string hexString, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(hexString))
            return;

        var cleaned = System.Text.RegularExpressions.Regex.Replace(hexString, @"[^0-9A-Fa-f]", "");
        if (cleaned.Length % 2 != 0)
            throw new ArgumentException("Hex string length must be even");

        var bytes = new byte[cleaned.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(cleaned.Substring(i * 2, 2), 16);
        }

        await SendAsync(bytes, cancellationToken);
    }

    private void StartReading()
    {
        _readCts = new CancellationTokenSource();
        _readTask = Task.Run(() => ReadLoop(_readCts.Token));
        _logger.LogDebug("Serial read loop started");
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
        _logger.LogDebug("Serial read loop stopped");
    }

    private async Task ReadLoop(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];

        while (!cancellationToken.IsCancellationRequested && _serialPort != null && _serialPort.IsOpen)
        {
            try
            {
                int bytesRead = await _serialPort.BaseStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);

                if (bytesRead > 0)
                {
                    var data = new byte[bytesRead];
                    Buffer.BlockCopy(buffer, 0, data, 0, bytesRead);

                    _logger.LogDebug("Received {Bytes} bytes from {Port}", bytesRead, CurrentPortName);
                    DataReceived?.Invoke(this, new Core.Communication.SerialDataReceivedEventArgs(data));
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (TimeoutException)
            {
                // ignore
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading from serial port");
                await Task.Delay(50, cancellationToken);
            }
        }
    }

    private static Dictionary<string, string> GetPortDescriptions()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Caption FROM Win32_SerialPort");

            foreach (ManagementObject obj in searcher.Get())
            {
                string? deviceId = obj["DeviceID"]?.ToString();
                string? caption = obj["Caption"]?.ToString();

                if (!string.IsNullOrWhiteSpace(deviceId) && !string.IsNullOrWhiteSpace(caption))
                {
                    result[deviceId] = caption;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WMI serial port query failed: {ex.Message}");
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
