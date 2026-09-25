// <copyright file="PythonProcessBridge.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PencilBox.Core.Models;
using PencilBox.Core.Services;

namespace PencilBox.Infrastructure.Dsp;

/// <summary>
/// Python 进程调用方式实现 DSP 服务（方案三）
/// 通过启动 Python 子进程，以 JSON over stdin/stdout 通信
/// </summary>
public class PythonProcessBridge : IDspService
{
    private readonly ILogger<PythonProcessBridge> _logger;
    private readonly string _pythonPath;
    private readonly string _scriptsDir;
    private readonly string _bridgePath;
    private readonly int _timeoutMs;
    private readonly JsonSerializerOptions _jsonOptions;

    private static readonly string[] RequiredPackages = { "numpy", "scipy" };

    public event EventHandler<DspProgressEventArgs>? ProgressChanged;

    public PythonProcessBridge(
        ILogger<PythonProcessBridge> logger,
        string? pythonPath = null,
        string? scriptsDir = null,
        int timeoutMs = 30000)
    {
        _logger = logger;
        _pythonPath = pythonPath ?? FindPython();
        _scriptsDir = scriptsDir ?? FindDefaultScriptsDir();
        _bridgePath = Path.Combine(_scriptsDir, "bridge.py");
        _timeoutMs = timeoutMs;

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false
        };
    }

    public async Task<DspEnvironmentInfo> CheckEnvironmentAsync(CancellationToken ct = default)
    {
        var errors = new List<string>();
        var packages = new Dictionary<string, bool>();

        // Check Python availability
        string version = string.Empty;
        bool pythonOk = false;

        try
        {
            var (stdout, stderr, exitCode) = await RunProcessAsync(
                _pythonPath, "--version", ct);

            if (exitCode == 0)
            {
                pythonOk = true;
                version = stdout.Trim();
            }
            else
            {
                errors.Add($"Python --version failed: {stderr}");
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Python not found at '{_pythonPath}': {ex.Message}");
        }

        // Check packages
        if (pythonOk)
        {
            foreach (var pkg in RequiredPackages)
            {
                try
                {
                    var (stdout, stderr, exitCode) = await RunProcessAsync(
                        _pythonPath, $"-c \"import {pkg}; print({pkg}.__version__)\"", ct);
                    packages[pkg] = exitCode == 0;
                }
                catch
                {
                    packages[pkg] = false;
                }
            }
        }

        // Check bridge.py exists
        if (!File.Exists(_bridgePath))
        {
            errors.Add($"bridge.py not found at {_bridgePath}");
        }

        return new DspEnvironmentInfo(
            pythonOk && errors.Count == 0,
            version,
            _pythonPath,
            packages,
            errors);
    }

    public IReadOnlyList<DspScriptInfo> GetAvailableScripts()
    {
        var scripts = new List<DspScriptInfo>();

        if (!Directory.Exists(_scriptsDir))
        {
            _logger.LogWarning("Scripts directory not found: {Dir}", _scriptsDir);
            return scripts;
        }

        var scriptSubDir = Path.Combine(_scriptsDir, "scripts");
        var searchDir = Directory.Exists(scriptSubDir) ? scriptSubDir : _scriptsDir;

        foreach (var file in Directory.GetFiles(searchDir, "*.py"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name == "__init__" || name == "bridge" || name.StartsWith("_"))
                continue;

            scripts.Add(new DspScriptInfo(
                name,
                $"DSP script: {name}",
                new List<DspParamInfo>(),
                SupportsStreaming: false));
        }

        return scripts;
    }

    public async Task<DspResult> ExecuteAsync(DspRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        ProgressChanged?.Invoke(this, new DspProgressEventArgs(
            $"Executing {request.ScriptName}", 0, null));

        try
        {
            // Prepare input data
            string? tempFile = null;
            string inputPath = request.InputFilePath ?? string.Empty;

            if (request.InputData != null && request.InputData.Length > 0)
            {
                tempFile = Path.GetTempFileName();
                await File.WriteAllBytesAsync(tempFile, request.InputData, ct);
                inputPath = tempFile;
            }

            // Build JSON request for bridge.py
            var payload = new Dictionary<string, object>
            {
                ["script"] = request.ScriptName,
                ["params"] = request.Parameters ?? new Dictionary<string, object>(),
                ["input_file"] = inputPath
            };

            var jsonRequest = JsonSerializer.Serialize(payload, _jsonOptions);

            // Execute Python bridge
            var (stdout, stderr, exitCode) = await RunProcessWithStdinAsync(
                _pythonPath,
                $"\"{_bridgePath}\"",
                jsonRequest,
                ct);

            // Cleanup temp file
            if (tempFile != null && File.Exists(tempFile))
                File.Delete(tempFile);

            sw.Stop();

            if (exitCode != 0)
            {
                return new DspResult(false, null, null,
                    $"Python exited with code {exitCode}: {stderr}", sw.Elapsed);
            }

            // Parse response
            var responseDoc = JsonDocument.Parse(stdout);
            var root = responseDoc.RootElement;

            string status = root.GetProperty("status").GetString() ?? "error";

            if (status != "ok")
            {
                string error = root.TryGetProperty("error", out var errEl)
                    ? errEl.GetString() ?? "Unknown error"
                    : "Unknown error";
                return new DspResult(false, null, null, error, sw.Elapsed);
            }

            // Extract result data
            Dictionary<string, object>? data = null;
            if (root.TryGetProperty("result", out var resultEl))
            {
                data = DeserializeToDict(resultEl);
            }

            ProgressChanged?.Invoke(this, new DspProgressEventArgs(
                "Completed", 100, null));

            return new DspResult(true, data, null, null, sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "DSP execution failed for script {Script}", request.ScriptName);
            return new DspResult(false, null, null, ex.Message, sw.Elapsed);
        }
    }

    public async Task<DspFullReport> AnalyzeFullAsync(
        string inputFilePath,
        DspAnalysisOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new DspAnalysisOptions { GeneratePlots = true };

        ProgressChanged?.Invoke(this, new DspProgressEventArgs("Starting full analysis", 0, null));

        var plots = new Dictionary<string, string>();

        // Step 1: Waveform stats (R1)
        ProgressChanged?.Invoke(this, new DspProgressEventArgs("Waveform analysis (R1)", 10, null));
        var waveform = await ExecuteAsync(new DspRequest(
            "waveform_render", inputFilePath, null, null,
            new Dictionary<string, object>
            {
                ["sample_rate"] = options.SampleRate,
                ["generate_image"] = options.GeneratePlots
            }), ct);
        if (options.GeneratePlots && waveform.Data != null &&
            waveform.Data.TryGetValue("image_base64", out var waveformImg))
        {
            plots["waveform"] = waveformImg?.ToString() ?? string.Empty;
            waveform.Data.Remove("image_base64");
        }

        // Step 2: FFT spectrum (R2)
        ProgressChanged?.Invoke(this, new DspProgressEventArgs("FFT analysis (R2)", 25, null));
        var fft = await ExecuteAsync(new DspRequest(
            "fft_analysis", inputFilePath, null, null,
            new Dictionary<string, object>
            {
                ["sample_rate"] = options.SampleRate,
                ["generate_image"] = options.GeneratePlots
            }), ct);
        if (options.GeneratePlots && fft.Data != null &&
            fft.Data.TryGetValue("image_base64", out var fftImg))
        {
            plots["spectrum"] = fftImg?.ToString() ?? string.Empty;
            fft.Data.Remove("image_base64");
        }

        // Step 3: VAD (R4)
        ProgressChanged?.Invoke(this, new DspProgressEventArgs("VAD detection (R4)", 45, null));
        var vad = await ExecuteAsync(new DspRequest(
            "vad_detect", inputFilePath, null, null, null), ct);

        // Step 4: Filter design (R5/R7)
        ProgressChanged?.Invoke(this, new DspProgressEventArgs("Filter design (R5/R7)", 65, null));
        var filter = await ExecuteAsync(new DspRequest(
            "fir_design", inputFilePath, null, null,
            new Dictionary<string, object>
            {
                ["max_taps"] = options.MaxFirTaps ?? 64,
                ["sample_rate"] = options.SampleRate
            }), ct);

        // Step 5: AGC analysis (R8)
        ProgressChanged?.Invoke(this, new DspProgressEventArgs("AGC analysis (R8)", 85, null));
        var agc = await ExecuteAsync(new DspRequest(
            "agc_analyze", inputFilePath, null, null, null), ct);

        ProgressChanged?.Invoke(this, new DspProgressEventArgs("Analysis complete", 100, null));

        // Build summary
        string summary = "Full analysis completed";
        if (fft.Success && fft.Data != null &&
            fft.Data.TryGetValue("dominant_freq", out var freq))
        {
            summary = $"Dominant frequency: {freq} Hz";
        }

        return new DspFullReport(waveform, fft, vad, filter, agc, null, summary, plots.Count > 0 ? plots : null);
    }

    public async ValueTask DisposeAsync()
    {
        await ValueTask.CompletedTask;
    }

    // === Private helpers ===

    private async Task<(string stdout, string stderr, int exitCode)> RunProcessAsync(
        string fileName, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        return (await stdoutTask, await stderrTask, process.ExitCode);
    }

    private async Task<(string stdout, string stderr, int exitCode)> RunProcessWithStdinAsync(
        string fileName, string arguments, string stdinData, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Write stdin
        await process.StandardInput.WriteLineAsync(stdinData);
        process.StandardInput.Close();

        // Read with timeout
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        var exitTask = process.WaitForExitAsync(ct);
        var completed = await Task.WhenAny(exitTask, Task.Delay(_timeoutMs, ct));

        if (completed != exitTask)
        {
            try { process.Kill(true); } catch { }
            return (string.Empty, $"Timeout after {_timeoutMs}ms", -1);
        }

        return (await stdoutTask, await stderrTask, process.ExitCode);
    }

    private static object? DeserializeElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => DeserializeToDict(element),
            JsonValueKind.Array => element.EnumerateArray().Select(DeserializeElement).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText()
        };
    }

    private static Dictionary<string, object>? DeserializeToDict(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        var result = new Dictionary<string, object>();
        foreach (var prop in element.EnumerateObject())
        {
            result[prop.Name] = DeserializeElement(prop.Value)!;
        }
        return result;
    }

    private static string FindPython()
    {
        // Try common Python paths
        string[] candidates = { "python", "python3", "py" };
        foreach (var cmd in candidates)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = cmd,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p != null && p.WaitForExit(3000) && p.ExitCode == 0)
                    return cmd;
            }
            catch { }
        }
        return "python";
    }

    private static string FindDefaultScriptsDir()
    {
        // Look for dsp/ directory relative to the application
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "dsp"),
            Path.Combine(baseDir, "..", "..", "..", "..", "dsp"),
            Path.Combine(Directory.GetCurrentDirectory(), "dsp")
        };

        foreach (var dir in candidates)
        {
            if (Directory.Exists(dir))
                return Path.GetFullPath(dir);
        }

        return Path.Combine(baseDir, "dsp");
    }
}
