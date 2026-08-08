using PencilBox.Cli.Output;
using PencilBox.Core.Communication;

namespace PencilBox.Cli.Commands;

public class SerialCommands
{
    private readonly ISerialTransport _serialTransport;

    public SerialCommands(ISerialTransport serialTransport)
    {
        _serialTransport = serialTransport;
    }

    public async Task<int> ListAsync()
    {
        var ports = await _serialTransport.EnumeratePortsAsync();
        ConsoleFormatter.Info($"Found {ports.Count} serial port(s)");
        
        ConsoleFormatter.PrintTable(ports,
            ("Port", p => p.PortName),
            ("Description", p => p.Description),
            ("Display", p => p.Display));
        return 0;
    }

    public async Task<int> OpenAsync(string portName, int baudRate)
    {
        var settings = new SerialPortSettings { BaudRate = baudRate };
        var ok = await _serialTransport.ConnectAsync(portName, settings);
        if (ok)
            ConsoleFormatter.Success($"Opened {portName} @ {baudRate}bps");
        else
        {
            ConsoleFormatter.Error($"Failed to open {portName}");
            return 1;
        }
        return 0;
    }

    public async Task<int> SendAsync(string? hex, string? text)
    {
        if (!_serialTransport.IsConnected)
        {
            ConsoleFormatter.Error("Serial port not open");
            return 1;
        }

        if (!string.IsNullOrEmpty(hex))
            await _serialTransport.SendHexAsync(hex);
        else if (!string.IsNullOrEmpty(text))
            await _serialTransport.SendTextAsync(text);
        else
        {
            ConsoleFormatter.Error("Provide --hex or --text");
            return 1;
        }

        ConsoleFormatter.Success("Sent");
        return 0;
    }

    public async Task<int> MonitorAsync(int duration)
    {
        if (!_serialTransport.IsConnected)
        {
            ConsoleFormatter.Error("Serial port not open");
            return 1;
        }

        ConsoleFormatter.Info($"Monitoring serial for {duration}s...");
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(duration));
        
        _serialTransport.DataReceived += (sender, e) =>
        {
            var hex = string.Join(" ", e.Data.Select(b => $"{b:X2}"));
            var text = System.Text.Encoding.UTF8.GetString(e.Data);
            Console.WriteLine($"[RX {e.Data.Length}B] HEX: {hex}");
            Console.WriteLine($"  TXT: {text}");
        };

        try { await Task.Delay(Timeout.Infinite, cts.Token); }
        catch (OperationCanceledException) { }

        ConsoleFormatter.Info("Monitor stopped");
        return 0;
    }

    public async Task<int> CloseAsync()
    {
        await _serialTransport.DisconnectAsync();
        ConsoleFormatter.Success("Closed");
        return 0;
    }
}
