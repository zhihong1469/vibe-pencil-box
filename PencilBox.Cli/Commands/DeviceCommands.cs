using Microsoft.Extensions.Logging;
using PencilBox.Cli.Output;
using PencilBox.Core.Communication;
using PencilBox.Core.Protocol;
using PencilBox.Core.Services;

namespace PencilBox.Cli.Commands;

public class DeviceCommands
{
    private readonly IDeviceService _deviceService;
    private readonly IHidTransport _hidTransport;

    public DeviceCommands(IDeviceService deviceService, IHidTransport hidTransport)
    {
        _deviceService = deviceService;
        _hidTransport = hidTransport;
    }

    public async Task<int> ScanAsync(ushort? vid, ushort? pid)
    {
        var devices = await _hidTransport.EnumerateDevicesAsync();
        
        var filtered = devices.AsEnumerable();
        if (vid.HasValue)
            filtered = filtered.Where(d => d.VendorId == vid.Value);
        if (pid.HasValue)
            filtered = filtered.Where(d => d.ProductId == pid.Value);

        var list = filtered.ToList();
        ConsoleFormatter.Info($"Found {list.Count} HID device(s)");
        
        ConsoleFormatter.PrintTable(list,
            ("VID", d => $"0x{d.VendorId:X4}"),
            ("PID", d => $"0x{d.ProductId:X4}"),
            ("Name", d => d.ProductName),
            ("Path", d => TruncatePath(d.DevicePath)));
        
        return 0;
    }

    public async Task<int> ConnectAsync(string? devicePath, ushort? vid, ushort? pid)
    {
        if (string.IsNullOrEmpty(devicePath) && (vid.HasValue || pid.HasValue))
        {
            var devices = await _hidTransport.EnumerateDevicesAsync();
            var match = devices.FirstOrDefault(d => 
                (!vid.HasValue || d.VendorId == vid.Value) &&
                (!pid.HasValue || d.ProductId == pid.Value));
            if (match == null)
            {
                ConsoleFormatter.Error("No device matching VID/PID found");
                return 1;
            }
            devicePath = match.DevicePath;
        }

        if (string.IsNullOrEmpty(devicePath))
        {
            ConsoleFormatter.Error("Device path or VID/PID required");
            return 1;
        }

        var connected = await _hidTransport.ConnectAsync(devicePath);
        if (connected)
        {
            ConsoleFormatter.Success($"Connected: {_hidTransport.CurrentDevice?.ProductName}");
            await Task.Delay(200);
            await _deviceService.ConnectAsync(devicePath);
        }
        else
        {
            ConsoleFormatter.Error("Failed to connect");
            return 1;
        }
        return 0;
    }

    public async Task<int> InfoAsync()
    {
        if (!_deviceService.IsConnected)
        {
            ConsoleFormatter.Error("Not connected. Use 'pencilbox device connect' first.");
            return 1;
        }

        var info = await _deviceService.GetDeviceInfoAsync();
        if (info == null)
        {
            ConsoleFormatter.Info("No device info available (timeout or no response)");
            return 0;
        }

        ConsoleFormatter.PrintObject(info);
        return 0;
    }

    public async Task<int> DisconnectAsync()
    {
        await _deviceService.DisconnectAsync();
        ConsoleFormatter.Success("Disconnected");
        return 0;
    }

    public async Task<int> MonitorAsync(byte? reportId, int duration)
    {
        if (!_hidTransport.IsConnected)
        {
            ConsoleFormatter.Error("Not connected. Use 'pencilbox device connect' first.");
            return 1;
        }

        ConsoleFormatter.Info($"Monitoring HID reports for {duration}s... (Ctrl+C to stop)");

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(duration));
        _hidTransport.ReportReceived += (sender, e) =>
        {
            if (reportId.HasValue && e.ReportId != reportId.Value)
                return;

            var hexStr = string.Join(" ", e.Data.Take(16).Select(b => $"{b:X2}"));
            var decoded = HidReportDecoder.DecodeAuto(e.ReportId, e.Data);
            Console.WriteLine($"[{e.Timestamp:HH:mm:ss.fff}] RID=0x{e.ReportId:X2} [{e.Data.Length,3}B] {hexStr}{(e.Data.Length > 16 ? "..." : "")}");
            if (!string.IsNullOrEmpty(decoded))
                Console.WriteLine($"  => {decoded}");
        };

        try { await Task.Delay(Timeout.Infinite, cts.Token); }
        catch (OperationCanceledException) { }

        ConsoleFormatter.Info("Monitor stopped");
        return 0;
    }

    public async Task<int> SendRawAsync(byte reportId, string hexData)
    {
        if (!_hidTransport.IsConnected)
        {
            ConsoleFormatter.Error("Not connected");
            return 1;
        }

        var data = ParseHex(hexData);
        await _hidTransport.SendReportAsync(reportId, data);
        ConsoleFormatter.Success($"Sent {data.Length} bytes on RID 0x{reportId:X2}");
        return 0;
    }

    public async Task<int> SendFeatureAsync(byte reportId, string hexData)
    {
        if (!_hidTransport.IsConnected)
        {
            ConsoleFormatter.Error("Not connected");
            return 1;
        }

        var data = ParseHex(hexData);
        await _hidTransport.SendFeatureReportAsync(reportId, data);
        ConsoleFormatter.Success($"Sent Feature Report {data.Length} bytes on RID 0x{reportId:X2}");
        return 0;
    }

    private static byte[] ParseHex(string hex)
    {
        var cleaned = hex.Replace(" ", "").Replace("\t", "");
        var bytes = new byte[cleaned.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(cleaned.Substring(i * 2, 2), 16);
        return bytes;
    }

    private static string TruncatePath(string path)
    {
        return path.Length > 50 ? "..." + path[^47..] : path;
    }
}
