using Microsoft.Extensions.Logging;
using PencilBox.Cli.Output;
using PencilBox.Core.Services;

namespace PencilBox.Cli.Commands;

public class AudioCommands
{
    private readonly IAudioService _audioService;

    public AudioCommands(IAudioService audioService)
    {
        _audioService = audioService;
    }

    public async Task<int> DevicesAsync()
    {
        var devices = await _audioService.GetAvailableDevicesAsync();
        ConsoleFormatter.Info($"Found {devices.Count} audio input device(s)");

        ConsoleFormatter.PrintTable(devices,
            ("USB", d => d.IsUsbDevice ? "*" : ""),
            ("VID", d => d.VendorId > 0 ? $"0x{d.VendorId:X4}" : "-"),
            ("PID", d => d.ProductId > 0 ? $"0x{d.ProductId:X4}" : "-"),
            ("Name", d => d.FriendlyName),
            ("ID", d => Truncate(d.DeviceId)));
        return 0;
    }

    public async Task<int> CaptureAsync(string? deviceId, int seconds, string? outputPath)
    {
        outputPath ??= Path.Combine(Path.GetTempPath(), $"pencilbox_capture_{DateTime.Now:yyyyMMdd_HHmmss}.wav");

        var format = new Core.Models.AudioFormat { SampleRate = 16000, Channels = 1, BitsPerSample = 16 };
        
        ConsoleFormatter.Info($"Capturing {seconds}s of audio...");
        await _audioService.StartCaptureAsync(deviceId, format);
        
        await Task.Delay(seconds * 1000);
        
        await _audioService.StopCaptureAsync();
        await _audioService.SaveToFileAsync(outputPath);

        var stats = _audioService.GetStats();
        ConsoleFormatter.Success($"Saved: {outputPath}");
        ConsoleFormatter.PrintObject(new
        {
            Duration = $"{stats.DurationSeconds:F1}s",
            BytesReceived = stats.TotalBytesReceived,
            LostRatio = $"{stats.LostRatio * 100:F2}%",
            GlitchCount = stats.GlitchCount,
            SampleRate = stats.SampleRate,
            Channels = stats.Channels,
            BitsPerSample = stats.BitsPerSample
        });
        return 0;
    }

    public async Task<int> MonitorAsync(string? deviceId, int duration)
    {
        var format = new Core.Models.AudioFormat { SampleRate = 16000, Channels = 1, BitsPerSample = 16 };
        var statsUpdated = 0;

        _audioService.StatsUpdated += (sender, stats) =>
        {
            if (Interlocked.Increment(ref statsUpdated) % 10 == 0)
            {
                var levelBar = new string('#', (int)(stats.CurrentLevel * 40));
                Console.Write($"\r{levelBar,-40} RMS={stats.CurrentLevel:F3} Glitch={stats.GlitchCount}  ");
            }
        };

        ConsoleFormatter.Info($"Monitoring audio for {duration}s...");
        await _audioService.StartCaptureAsync(deviceId, format);
        
        await Task.Delay(duration * 1000);
        
        await _audioService.StopCaptureAsync();
        Console.WriteLine();
        var finalStats = _audioService.GetStats();
        ConsoleFormatter.Info($"Glitches: {finalStats.GlitchCount}, Lost: {finalStats.LostRatio * 100:F2}%");
        return 0;
    }

    private static string Truncate(string s) => s.Length > 40 ? s[..37] + "..." : s;
}
