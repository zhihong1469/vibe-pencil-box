using System.Text.Json;
using PencilBox.Cli.Output;
using PencilBox.Core.Models;
using PencilBox.Core.Services;

namespace PencilBox.Cli.Commands;

public class DspCommands
{
    private readonly IDspService _dspService;

    public DspCommands(IDspService dspService)
    {
        _dspService = dspService;
    }

    public async Task<int> CheckAsync()
    {
        ConsoleFormatter.Info("Checking Python environment...");
        var env = await _dspService.CheckEnvironmentAsync();

        ConsoleFormatter.PrintObject(new
        {
            PythonAvailable = env.PythonAvailable,
            PythonVersion = env.PythonVersion,
            PythonPath = env.PythonPath,
            PackageCount = env.Packages.Count,
            Errors = env.Errors.Count
        });

        if (env.Packages.Count > 0)
        {
            ConsoleFormatter.Info("Packages:");
            foreach (var (name, ok) in env.Packages)
                Console.WriteLine($"  {name,-15} {(ok ? "OK" : "MISSING")}");
        }

        if (env.Errors.Count > 0)
        {
            foreach (var err in env.Errors)
                ConsoleFormatter.Error(err);
        }

        return env.PythonAvailable ? 0 : 1;
    }

    public int Scripts()
    {
        var scripts = _dspService.GetAvailableScripts();
        ConsoleFormatter.Info($"Found {scripts.Count} DSP script(s)");
        
        ConsoleFormatter.PrintTable(scripts,
            ("Name", s => s.Name),
            ("Streaming", s => s.SupportsStreaming ? "Y" : "N"),
            ("Description", s => s.Description));
        return 0;
    }

    public async Task<int> RunAsync(string script, string input, string? paramsJson)
    {
        Dictionary<string, object>? parameters = null;
        if (!string.IsNullOrEmpty(paramsJson))
        {
            var doc = JsonDocument.Parse(paramsJson);
            parameters = new Dictionary<string, object>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                parameters[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.Number => prop.Value.GetDouble(),
                    JsonValueKind.String => prop.Value.GetString() ?? "",
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => prop.Value.GetRawText()
                };
            }
        }

        ConsoleFormatter.Info($"Running DSP script: {script}");
        _dspService.ProgressChanged += (s, e) =>
        {
            if (e.Percent > 0)
                ConsoleFormatter.Info($"  [{e.Percent,3}%] {e.CurrentStep}");
        };

        var result = await _dspService.ExecuteAsync(new DspRequest(script, input, null, null, parameters));

        if (result.Success)
        {
            ConsoleFormatter.Success($"Completed in {result.Duration.TotalMilliseconds:F0}ms");
            if (result.Data != null)
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                Console.WriteLine(JsonSerializer.Serialize(result.Data, options));
            }
        }
        else
        {
            ConsoleFormatter.Error($"Failed: {result.Error}");
            return 1;
        }
        return 0;
    }

    public async Task<int> AnalyzeAsync(string input, string? output)
    {
        ConsoleFormatter.Info($"Full analysis: {input}");
        _dspService.ProgressChanged += (s, e) =>
        {
            ConsoleFormatter.Info($"  [{e.Percent,3}%] {e.CurrentStep}");
        };

        var report = await _dspService.AnalyzeFullAsync(input);

        ConsoleFormatter.Success("Analysis complete");
        Console.WriteLine($"  Summary: {report.Summary}");

        var results = new Dictionary<string, object?>
        {
            ["waveform"] = report.WaveformStats?.Data,
            ["fft"] = report.FftSpectrum?.Data,
            ["vad"] = report.VadResult?.Data,
            ["filter"] = report.FilterDesign?.Data,
            ["agc"] = report.AgcRecommendation?.Data
        };

        if (!string.IsNullOrEmpty(output))
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(results, options));
            ConsoleFormatter.Success($"Report saved: {output}");
        }

        return 0;
    }
}
