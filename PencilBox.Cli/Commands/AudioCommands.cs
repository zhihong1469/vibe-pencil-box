using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PencilBox.Cli.Output;
using PencilBox.Core.Dsp;
using PencilBox.Core.Models;
using PencilBox.Core.Services;
using PencilBox.Infrastructure.Audio;

namespace PencilBox.Cli.Commands;

public class AudioCommands
{
    private readonly IAudioService _audioService;
    private readonly IAudioPassthrough _passthrough;
    private readonly LatencyProbeEngine _probe;

    public AudioCommands(IAudioService audioService, IAudioPassthrough passthrough, LatencyProbeEngine probe)
    {
        _audioService = audioService;
        _passthrough = passthrough;
        _probe = probe;
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
        // 默认落盘 output/audio（识别场景素材约定目录）；--file 可覆盖（--output/-o 被全局格式选项占用）
        outputPath ??= Path.Combine("output", "audio", $"rec_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

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
            Device = string.IsNullOrEmpty(stats.DeviceName) ? "(未知)" : stats.DeviceName,
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

    // === 延迟测量底座（box/07 §2；box/03 方案落地） ===

    /// <summary>
    /// 端到端延迟探测：扬声器播扫频脉冲串 → 采集端同录 → 双侧 QPC + 互相关定位 →
    /// 闭环延迟 p50/p95/min/max。自校准：播/录本机双端点（如 --in Realtek --out Realtek）。
    /// 设备端（笔+基站）接入后同一设施直接测 P3/M3。
    /// </summary>
    public async Task<int> LatencyProbeAsync(string? inSelector, string? outSelector,
        int pulses, int intervalMs, int pulseMs, float amplitude,
        int renderLatencyMs, bool raw, string? saveCapture, string? reportOut)
    {
        var result = await _probe.RunAsync(inSelector, outSelector, pulses, intervalMs, pulseMs,
            amplitude, renderLatencyMs, progress: m => ConsoleFormatter.Info(m));

        if (saveCapture is { Length: > 0 })
        {
            WavFile.WriteMono16(saveCapture, result.CapturedMono, result.CaptureRate);
            ConsoleFormatter.Info($"采集流已存：{saveCapture}");
        }

        // 检测模板须与播放同形（不同采样率各自生成）
        var template = PulseTrain.GenerateChirp(result.CaptureRate, pulseMs, PulseTrain.DefaultF0Hz, PulseTrain.DefaultF1Hz, amplitude);
        var intervalSamples = result.CaptureRate * intervalMs / 1000;
        var hits = PulseLocator.Locate(result.CapturedMono, template, pulses, intervalSamples * 3 / 4);

        var qpf = (double)Stopwatch.Frequency;
        // 闭环 = 渲染缓冲(常量) + 声路 + 采集缓冲(常量)；默认扣掉渲染缓冲已知量，--raw 看原始闭环
        var baseOffsetMs = (result.CaptureQpc - result.PlayQpc) / qpf * 1000.0 - (raw ? 0 : result.RenderLatencyMs);
        var delays = new List<double>();
        for (var k = 0; k < Math.Min(hits.Count, result.PulsePositionsRender.Length); k++)
        {
            var arrivalMs = baseOffsetMs + hits[k].PositionSamples * 1000.0 / result.CaptureRate;
            var emittedMs = result.PulsePositionsRender[k] * 1000.0 / result.RenderRate;
            delays.Add(arrivalMs - emittedMs);
        }

        if (delays.Count == 0)
        {
            ConsoleFormatter.Error("未检测到任何脉冲：确认采集端点能听到扬声器（音量/端点选择/静音），" +
                                   "或用 --save-capture 落盘排查。");
            return 2;
        }
        if (hits.Count < pulses)
            ConsoleFormatter.Error($"仅定位到 {hits.Count}/{pulses} 个脉冲（弱信号/没对准的样本已丢弃）；统计基于有效样本。");

        var sorted = delays.Order().ToArray();
        double P(double q) => sorted[(int)Math.Clamp(q * (sorted.Length - 1), 0, sorted.Length - 1)];
        ConsoleFormatter.Success($"闭环延迟（n={delays.Count}）：p50 {P(0.5):F1}ms | p95 {P(0.95):F1}ms | " +
                                 $"min {sorted[0]:F1}ms | max {sorted[^1]:F1}ms（抖动 p95−p50 = {P(0.95) - P(0.5):F1}ms）");
        ConsoleFormatter.PrintObject(new
        {
            Capture = $"{result.CaptureDevice} @ {result.CaptureRate}Hz",
            Render = $"{result.RenderDevice} @ {result.RenderRate}Hz/{result.RenderChannels}ch",
            RenderLatencySubtracted = raw ? 0 : result.RenderLatencyMs,
            Pulses = $"{hits.Count}/{pulses} 定位",
            PerPulseMs = string.Join(", ", delays.Select(d => d.ToString("F1")))
        });

        if (reportOut is { Length: > 0 })
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(reportOut));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var report = new
            {
                capture = result.CaptureDevice,
                render = result.RenderDevice,
                captureRate = result.CaptureRate,
                renderRate = result.RenderRate,
                renderLatencyMs = raw ? 0 : result.RenderLatencyMs,
                pulsesLocated = hits.Count,
                pulsesSent = pulses,
                p50Ms = Math.Round(P(0.5), 2),
                p95Ms = Math.Round(P(0.95), 2),
                minMs = Math.Round(sorted[0], 2),
                maxMs = Math.Round(sorted[^1], 2),
                delaysMs = delays.Select(d => Math.Round(d, 2)).ToArray()
            };
            await File.WriteAllTextAsync(reportOut, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            ConsoleFormatter.Info($"报告已写：{reportOut}");
        }
        return 0;
    }

    /// <summary>
    /// WAV 包络分析（离线）：整体电平 + 逐块信封 + 首个出声时刻/尾音长度。
    /// 服务基站批次 1 验收②（起播阈值代理=录音文件首声时刻）与④（D6 尾音长度）。
    /// </summary>
    public Task<int> EnvelopeAsync(string? file, int thresholdDb, int blockMs, string? csvOut)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            ConsoleFormatter.Error("请给 --file <路径.wav>（本工具自录：output/audio/）");
            return Task.FromResult(1);
        }
        var wav = WavFile.ReadMono(file);
        var report = EnvelopeAnalyzer.Analyze(wav.Mono, wav.SampleRate, blockMs, thresholdDb);

        ConsoleFormatter.PrintObject(new
        {
            File = file,
            Format = $"{wav.SampleRate}Hz/{wav.Channels}ch/{wav.BitsPerSample}bit→mono",
            Total = $"{report.TotalSeconds:F1}s",
            PeakDbfs = report.PeakDbfs,
            RmsDbfs = report.RmsDbfs,
            Threshold = $"{thresholdDb}dBFS / {blockMs}ms 块",
            Speech = $"{report.SpeechSeconds:F1}s（{report.SpeechBlocks} 块）",
            FirstSoundMs = report.FirstSoundMs is { } f ? $"{f:F0}（起播代理）" : "未见出声",
            TailMs = report.TailMs is { } t ? $"{t:F0}（末声→文件尾；D6 验收看这个）" : "整段无声"
        });

        if (csvOut is { Length: > 0 })
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(csvOut));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var lines = new List<string> { "time_ms,rms_dbfs,peak_dbfs" };
            lines.AddRange(report.Blocks.Select(b =>
                $"{b.StartSample * 1000.0 / report.SampleRate:F0},{b.RmsDbfs:F1},{b.PeakDbfs:F1}"));
            File.WriteAllLines(csvOut, lines);
            ConsoleFormatter.Info($"信封 CSV 已写：{csvOut}");
        }
        return Task.FromResult(0);
    }

    /// <summary>
    /// P2 带内 tick 统计（box/07 §2.4，笔端 02 §4 口径）：输入 CSV（每行 tick,arrival_ms），
    /// 按会话切段、线性拟合扣晶振漂移，输出单向延迟分布与重传率（+7.5n 阶跃占比）。
    /// </summary>
    public Task<int> TickStatsAsync(string? file, double stepMs, double sessionGapMs, bool json)
    {
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
        {
            ConsoleFormatter.Error($"请给 --file <ticks.csv>（每行 tick,arrival_ms；不存在：{file}）");
            return Task.FromResult(1);
        }

        var ticks = new List<double>();
        var arrivals = new List<double>();
        foreach (var line in File.ReadLines(file))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') ||
                trimmed.StartsWith("tick", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = trimmed.Split(',', ';', '\t');
            if (parts.Length < 2) continue;
            if (!double.TryParse(parts[0], out var tick) || !double.TryParse(parts[1], out var arrival)) continue;
            ticks.Add(tick);
            arrivals.Add(arrival);
        }
        if (ticks.Count == 0)
        {
            ConsoleFormatter.Error("CSV 未解析出任何 tick 样本（格式：tick,arrival_ms）");
            return Task.FromResult(1);
        }

        var report = TickStatsAnalyzer.Analyze(ticks, arrivals, sessionGapMs: sessionGapMs, stepMs: stepMs);
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                totalSamples = report.TotalSamples,
                stepMs,
                sessions = report.Sessions.Select(s => new
                {
                    s.Index,
                    s.Count,
                    baseDelayMs = Math.Round(s.BaseDelayMs, 3),
                    p95Ms = Math.Round(s.P95Ms, 3),
                    minMs = Math.Round(s.MinMs, 3),
                    maxMs = Math.Round(s.MaxMs, 3),
                    driftMsPerMin = Math.Round(s.DriftMsPerMin, 3),
                    s.RetransSamples,
                    retransRatio = Math.Round(s.RetransRatio, 4),
                    s.MaxSteps
                })
            }, new JsonSerializerOptions { WriteIndented = true }));
            return Task.FromResult(0);
        }

        ConsoleFormatter.Info($"P2 tick 统计：{report.TotalSamples} 样本 / {report.Sessions.Count} 会话 / 阶跃粒度 {stepMs}ms");
        ConsoleFormatter.PrintTable(report.Sessions,
            ("#", s => s.Index.ToString()),
            ("样本", s => s.Count.ToString()),
            ("基线p50", s => $"{s.BaseDelayMs:F2}ms"),
            ("p95", s => $"{s.P95Ms:F2}ms"),
            ("min", s => $"{s.MinMs:F2}ms"),
            ("max", s => $"{s.MaxMs:F2}ms"),
            ("漂移", s => $"{s.DriftMsPerMin:+0.00;-0.00}ms/min"),
            ("重传", s => $"{s.RetransSamples}（{s.RetransRatio * 100:F1}%）"),
            ("最大阶跃", s => s.MaxSteps > 0 ? $"+{s.MaxSteps}×{stepMs:g}ms" : "-"));
        return Task.FromResult(0);
    }

    // === 扩音直通（采集 → 扬声器实时回路） ===

    /// <summary>列出播放（渲染）设备</summary>
    public async Task<int> RendersAsync()
    {
        var devices = await _passthrough.GetRenderDevicesAsync();
        ConsoleFormatter.Info($"Found {devices.Count} audio render device(s)");

        ConsoleFormatter.PrintTable(devices,
            ("USB", d => d.IsUsbDevice ? "*" : ""),
            ("Name", d => d.FriendlyName),
            ("ID", d => Truncate(d.DeviceId)));
        return 0;
    }

    /// <summary>
    /// 端点音量自检/设置：读扬声器与麦克风的音量/静音；--set/--mic-set 调音量，--unmute/--mic-unmute 解除静音；
    /// --out/--in 按名称子串选端点（默认=系统默认设备；注意默认采集可能是远程软件虚拟声卡）。
    /// "跑了没声"多半是端点静音或音量 0，先看这里再怀疑引擎。
    /// </summary>
    public async Task<int> VolumeAsync(string? outSelector, string? inSelector,
        int? set, bool unmute, int? micSet, bool micUnmute)
    {
        await Task.CompletedTask;
        if (set.HasValue || unmute)
            AudioEndpointControl.SetVolume(isRender: true, outSelector, set ?? -1, unmute ? false : null);
        if (micSet.HasValue || micUnmute)
            AudioEndpointControl.SetVolume(isRender: false, inSelector, micSet ?? -1, micUnmute ? false : null);

        var (render, capture) = AudioEndpointControl.Describe(outSelector, inSelector);
        ConsoleFormatter.PrintObject(new
        {
            Render = $"{render.Name}：{render.VolumePercent}%{(render.Mute ? "【静音!】" : "")}",
            Capture = $"{capture.Name}：{capture.VolumePercent}%{(capture.Mute ? "【静音!】" : "")}"
        });
        return 0;
    }

    /// <summary>
    /// 扩音直通：录音设备 → 播放设备实时播出。
    /// duration=0 时一直运行直到 Ctrl+C；实时统计每 500ms 刷新一行。
    /// </summary>
    public async Task<int> PassthroughAsync(string? inDevice, string? outDevice,
        int rate, int latencyMs, float volume, int duration, bool aec = true, bool ans = true, bool afc = false,
        int aecSuppress = 0, int ansSuppress = 0, bool aecGate = false)
    {
        var captureDevices = await _passthrough.GetCaptureDevicesAsync();
        var renderDevices = await _passthrough.GetRenderDevicesAsync();
        var capture = ResolveDevice(captureDevices, inDevice, "录音");
        var render = ResolveDevice(renderDevices, outDevice, "播放");
        AudioPassthroughStats? liveStats = null;

        ConsoleFormatter.Info($"扩音：[{capture?.FriendlyName ?? "系统默认录音"}] → [{render?.FriendlyName ?? "系统默认播放"}]");
        ConsoleFormatter.Info($"{rate}Hz/16bit/1ch，渲染缓冲 {latencyMs}ms，音量 {volume:F2}x（>1 时限制器自动防破音）；" +
                              $"处理链 AEC={(aec ? 1 : 0)} ANS={(ans ? 1 : 0)} AFC={(afc ? 1 : 0)}" +
                              (aecSuppress > 0 ? $"（手动 {aecSuppress}dB）" : "") +
                              (ansSuppress > 0 ? $"（手动 {ansSuppress}dB）" : "") +
                              (aecGate ? "；AEC条件触发开（参考静音旁路）" : "") +
                              $"；实时统计每 500ms 刷新，Ctrl+C 停止。");
        if (duration > 0)
            ConsoleFormatter.Info($"将在 {duration}s 后自动停止。");

        void OnStats(object? _, AudioPassthroughStats s) => PrintLiveLine(s);
        _passthrough.StatsUpdated += OnStats;

        using var stopCts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;           // 接管 Ctrl+C：优雅停止而不是杀进程
            stopCts.Cancel();
        };

        try
        {
            await _passthrough.StartAsync(new PassthroughStartOptions
            {
                CaptureDeviceId = capture?.DeviceId,
                RenderDeviceId = render?.DeviceId,
                SampleRate = rate,
                Channels = 1,
                RenderLatencyMs = latencyMs,
                Volume = volume,
                EnableAec = aec,
                EnableAns = ans,
                EnableAfc = afc,
                AecAuto = aecSuppress <= 0,
                AecSuppressDb = aecSuppress > 0 ? aecSuppress : 40,
                AnsAuto = ansSuppress <= 0,
                AnsNoiseSuppressDb = ansSuppress > 0 ? ansSuppress : 15,
                EnableAecGate = aecGate
            });

            try
            {
                if (duration > 0)
                    await Task.Delay(TimeSpan.FromSeconds(duration), stopCts.Token);
                else
                    await Task.Delay(Timeout.Infinite, stopCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Ctrl+C 或时长到：正常收尾
            }
            // 停止前快照：StopAsync 会拆链，活量统计（链附加/环形深度）归零后取数就失真了
            liveStats = _passthrough.GetStats();
        }
        finally
        {
            _passthrough.StatsUpdated -= OnStats;
            await _passthrough.StopAsync();
        }

        Console.WriteLine();
        var stats = liveStats ?? _passthrough.GetStats();
        ConsoleFormatter.Success("扩音已停止，最终统计：");
        ConsoleFormatter.PrintObject(new
        {
            Duration = $"{stats.ElapsedSeconds:F1}s",
            Ring = $"avg {stats.RingDepthAvgMs:F1}ms / max {stats.RingDepthMaxMs:F1}ms",
            Underruns = stats.UnderrunEvents,
            DroppedBytes = stats.DroppedBytes,
            AecGateBypassMs = stats.AecGateBypassMs,
            LimiterGain = stats.LimiterGain < 0.995f ? $"{stats.LimiterGain:F2}（已触顶，加音量不再变大）" : "1.00（未触发）",
            EstimatedLatency = $"{stats.EstimatedLatencyMs:F0}ms（Box 段；设备端剩余预算 = 总目标 − 此值）",
            Dsp = $"AEC偏移 {stats.AecOffsetMs:F1}ms / 单帧最大耗时 {stats.DspMaxMs:F2}ms / 超帧 {stats.DspOverruns}",
            Capture = $"{stats.CaptureDevice} ({stats.CaptureFormat})",
            Render = $"{stats.RenderDevice} ({stats.RenderFormat})",
            stats.CaptureCallbacks,
            stats.RenderPulls
        });
        return 0;
    }

    private static AudioDeviceInfo? ResolveDevice(IReadOnlyList<AudioDeviceInfo> devices, string? selector, string label)
    {
        if (string.IsNullOrWhiteSpace(selector) || selector.Equals("default", StringComparison.OrdinalIgnoreCase))
            return null;

        var hit = devices.FirstOrDefault(d => d.DeviceId.Equals(selector, StringComparison.OrdinalIgnoreCase))
                  ?? devices.FirstOrDefault(d => d.FriendlyName.Contains(selector, StringComparison.OrdinalIgnoreCase))
                  ?? throw new ArgumentException($"未找到匹配的{label}设备: {selector}");
        return hit;
    }

    private static void PrintLiveLine(AudioPassthroughStats s)
    {
        var levelBar = new string('#', (int)(s.Level * 20));
        var ts = TimeSpan.FromSeconds(s.ElapsedSeconds);
        var lim = s.LimiterGain < 0.995f ? $"  lim {s.LimiterGain,4:F2}" : "";
        Console.Write($"\r[{ts:mm\\:ss}] ring avg {s.RingDepthAvgMs,5:F1}ms / max {s.RingDepthMaxMs,5:F1}ms  " +
                      $"drop {s.DroppedBytes,7}B  under {s.UnderrunEvents,4}  lvl |{levelBar,-20}|{lim}  box~{s.EstimatedLatencyMs,3:F0}ms ");
    }
}
