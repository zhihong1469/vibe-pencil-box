// <copyright file="SoftReceiverCommands.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Diagnostics;
using System.Globalization;
using NAudio.Wave;
using PencilBox.Cli.Output;
using PencilBox.Core.Ble;
using PencilBox.Core.Ble.SoftReceiver;
using PencilBox.Core.Communication;
using PencilBox.Core.Dsp;
using PencilBox.Core.Models;
using PencilBox.Core.Services;
using PencilBox.Infrastructure.Audio;

namespace PencilBox.Cli.Commands;

/// <summary>
/// 软件接收器命令面（形态③：PC 直连键盘收语音，绕过硬件基站）。
/// `srx scan` 找广播中的笔端 → `srx connect` 建连+svr1 核表 → `srx receive` 收流解码落 WAV
/// → `srx amp` 解码 PCM 泵进扩音引擎实时出声 → `srx batt` 查电量验下行/应答链。
/// 会话编排/解析/解码在 Core（<see cref="SoftReceiverSession"/>），
/// 本类只做目标解析、WAV 落盘、扩音泵接驳与文案。
/// 背景：链路上"基站蓄水池+USB"段的延迟冗余可由直连绕过（净收益以 chirp A/B 实测为准）；
/// 机制文档 = _share/box/docs/GitFlow/feature/soft-receiver/README.md。
/// </summary>
public class SoftReceiverCommands
{
    private readonly IBleCentral _central;
    private readonly IBleScanner _scanner;
    private readonly IAudioPassthrough _passthrough;

    public SoftReceiverCommands(IBleCentral central, IBleScanner scanner, IAudioPassthrough passthrough)
    {
        _central = central;
        _scanner = scanner;
        _passthrough = passthrough;
    }

    /// <summary>`srx scan`：扫描广播中的 SunVote 笔端（软件接收器候选目标）。</summary>
    public async Task<int> ScanAsync(int durationSec)
    {
        ConsoleFormatter.Info($"[SRX] 扫描 {durationSec}s 找笔端广播（SunVote-R1*）…");
        var devices = await _scanner.ScanAsync(durationSec);
        var pens = devices
            .Where(d => (d.LocalName ?? "").StartsWith(BleNameConstants.PenPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.Rssi)
            .ToList();

        if (pens.Count == 0)
        {
            ConsoleFormatter.Error("未发现笔端广播（键盘可能在基站连接态休眠不广播；" +
                                   "从基站拔出/断连后重试，或直接 srx connect --mac <地址>）");
            return 1;
        }

        ConsoleFormatter.Info($"发现 {pens.Count} 台笔端：");
        ConsoleFormatter.Info("RSSI  MAC                 Name                      Type");
        ConsoleFormatter.Info("----  ------------------  ------------------------  ----");
        foreach (var d in pens)
            ConsoleFormatter.Info($"{d.Rssi,4}  {d.MacDisplayText,-19}  {d.LocalName,-24}  {d.AddressType}");
        return 0;
    }

    /// <summary>`srx connect`：建连 + GATT 发现 + svr1 特征核表（验形态③ GATT 面在位）。</summary>
    public async Task<int> ConnectAsync(string? mac, string? name, int timeoutMs)
    {
        var macDisplay = await ResolveTargetAsync(mac, name);
        if (macDisplay == null)
            return 1;

        var session = new SoftReceiverSession(_central, msg => ConsoleFormatter.Info($"[SRX] {msg}"));
        try
        {
            await session.ConnectAsync(macDisplay);
            ConsoleFormatter.Success(
                $"已连接并核表：DATA={(session.DataCharacteristicFound ? "✓" : "✗")} " +
                $"CMD={(session.CmdCharacteristicFound ? "✓" : "✗")} " +
                $"RSP={(session.RspCharacteristicFound ? "✓" : "✗")}" +
                (session.DataCharacteristicFound ? "" : "（DATA 缺失无法收语音）"));
            return session.DataCharacteristicFound ? 0 : 1;
        }
        catch (InvalidOperationException ex)
        {
            ConsoleFormatter.Error(ex.Message);
            return 1;
        }
        finally
        {
            await _central.DisconnectAsync();
            ConsoleFormatter.Info("已断开");
        }
    }

    /// <summary>
    /// `srx receive`：收流解码落 WAV。语音须键盘自己在识别/扩音模式（或 --voice-state 触发），
    /// Ctrl+C 或 --sec 到点收尾出统计。
    /// </summary>
    public async Task<int> ReceiveAsync(string? mac, string? name, string? outPath, int sec,
        byte? voiceState, int timeoutMs)
    {
        var macDisplay = await ResolveTargetAsync(mac, name);
        if (macDisplay == null)
            return 1;

        outPath ??= $"srx_{DateTime.Now:yyyyMMdd_HHmmss}.wav";
        var session = new SoftReceiverSession(_central, msg => ConsoleFormatter.Info($"[SRX] {msg}"));
        var lastSeqJumpReport = 0L;

        using var cts = new CancellationTokenSource();
        if (sec > 0)
            cts.CancelAfter(TimeSpan.FromSeconds(sec));
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            await session.ConnectAsync(macDisplay);

            // 音频帧 → WAV（回调在 WinRT 线程池，写入加锁）
            long pcmBytes = 0;
            var waveFormat = new WaveFormat(MsbcDecoder.SampleRate, 16, 1);
            using (var writer = new WaveFileWriter(outPath, waveFormat))
            {
                var writeLock = new object();
                session.AudioFrameReady += (_, f) =>
                {
                    lock (writeLock)
                    {
                        writer.Write(f.Pcm, 0, f.Pcm.Length);
                        pcmBytes += f.Pcm.Length;
                    }
                };
                session.AudioSeqJump += (_, gap) =>
                {
                    if (Interlocked.Read(ref lastSeqJumpReport) != gap)
                        ConsoleFormatter.Warn($"[SRX] seq 跳变缺 {gap} 帧（会掩盖为静音）");
                };
                session.SwitchChanged += (_, s) =>
                    ConsoleFormatter.Info($"[SRX] 档位切换 → {s.Mode}（0=IDLE 1=识别 2=扩音）");
                session.KeyEvent += (_, k) =>
                    ConsoleFormatter.Info($"[SRX] 键 {(k.IsDown ? "DOWN" : "UP  ")} page=0x{k.Page:X2} code=0x{k.KeyCode:X2} mod=0x{k.Modifier:X2}");
                session.ResponseReceived += (_, r) =>
                    ConsoleFormatter.Info($"[SRX] RSP cmd=0x{r.CmdId:X2} status={r.Status} data={Convert.ToHexString(r.Data)}");

                if (voiceState is byte vs && !await session.SendVoiceStateAsync(vs))
                    ConsoleFormatter.Warn("[SRX] 无 CMD 特征，--voice-state 未下发（手动让键盘进识别/扩音模式）");

                ConsoleFormatter.Info($"[SRX] 收流中 → {outPath}（{(sec > 0 ? $"{sec}s 后" : "Ctrl+C")}收尾）…");
                await session.StartAsync(cts.Token);
            }

            var stats = session.Stats;
            ConsoleFormatter.Success($"收流完成：{outPath}（{pcmBytes / (double)MsbcDecoder.SampleRate / 2:F1}s PCM @16k mono）");
            ConsoleFormatter.Info($"[SRX] {stats.Summary()}");
            if (stats.AudioFramesDecoded > 0 && stats.CrcErrors == 0 && stats.SeqJumps == 0)
                ConsoleFormatter.Success("链路质量：CRC 零错、seq 零跳（BLE 链路干净）");
            else if (stats.AudioFramesDecoded == 0)
                ConsoleFormatter.Warn("零音频帧：语音流由物理动作触发——K2 语音键（点按/按住）或 S1 拨扩音档；" +
                                      "0x20 SET_VOICE_STATE 笔端未实现（当前固件只认 0x21/0x42），--voice-state 仅为协议预留");
            return stats.AudioFramesDecoded > 0 ? 0 : 2;
        }
        catch (InvalidOperationException ex)
        {
            ConsoleFormatter.Error(ex.Message);
            return 1;
        }
        finally
        {
            await _central.DisconnectAsync();
            ConsoleFormatter.Info("已断开");
        }
    }

    /// <summary>
    /// `srx amp`：软件接收器 → 扩音引擎实时出声（外源 PCM 泵，替代 WASAPI 采集）。
    /// 解码 PCM（16k/16bit/单声道）直入既有处理链（带通/AEC/ANS/AFC）→ 渲染。
    /// 钩子：音频帧到达节奏（均隙/最大隙）入终报——Windows BLE 通知缓冲若成批到达
    /// 会显形为最大隙远超 7.5ms，是"直连净收益"议题的关键证据。
    /// </summary>
    public async Task<int> AmpAsync(string? mac, string? name, int sec, byte? voiceState,
        float volume, int latencyMs, bool aec, bool ans, bool afc, int hpfHz, int lpfHz)
    {
        var macDisplay = await ResolveTargetAsync(mac, name);
        if (macDisplay == null)
            return 1;

        var session = new SoftReceiverSession(_central, msg => ConsoleFormatter.Info($"[SRX] {msg}"));
        long fedFrames = 0, rejectedFrames = 0, arrLast = 0, arrSumMs = 0, arrCount = 0, arrMaxMs = 0;
        var feedLock = new object();
        session.AudioFrameReady += (_, f) =>
        {
            long now = Environment.TickCount64;
            lock (feedLock)
            {
                if (arrLast != 0)
                {
                    var gap = now - arrLast;
                    arrSumMs += gap;
                    arrCount++;
                    if (gap > Interlocked.Read(ref arrMaxMs)) Interlocked.Exchange(ref arrMaxMs, gap);
                }
                arrLast = now;
                if (_passthrough.WriteExternalPcm(f.Pcm) == f.Pcm.Length)
                    fedFrames++;
                else
                    rejectedFrames++;
            }
        };
        session.SwitchChanged += (_, s) =>
            ConsoleFormatter.Info($"[SRX] 档位切换 → {s.Mode}（0=IDLE 1=识别 2=扩音）");

        using var cts = new CancellationTokenSource();
        if (sec > 0)
            cts.CancelAfter(TimeSpan.FromSeconds(sec));
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        AudioPassthroughStats? liveStats = null;
        try
        {
            await session.ConnectAsync(macDisplay);
            if (!session.CmdCharacteristicFound)
                ConsoleFormatter.Warn("[SRX] 无 CMD 特征：--voice-state 不可下发（手动让键盘进识别/扩音模式）");

            ConsoleFormatter.Info(
                $"[SRX-AMP] 外源泵扩音：音量 {volume:F2}x，渲染缓冲 {latencyMs}ms，" +
                $"处理链 AEC={(aec ? 1 : 0)} ANS={(ans ? 1 : 0)} AFC={(afc ? 1 : 0)}，" +
                $"带通 HPF={(hpfHz > 0 ? $"{hpfHz}Hz" : "关")} LPF={(lpfHz > 0 ? $"{lpfHz}Hz" : "关")}（{(sec > 0 ? $"{sec}s 后" : "Ctrl+C")}收尾）");
            await _passthrough.StartAsync(new PassthroughStartOptions
            {
                ExternalSource = true,
                RenderLatencyMs = latencyMs,
                Volume = volume,
                EnableAec = aec,
                EnableAns = ans,
                EnableAfc = afc,
                HpfHz = hpfHz,
                LpfHz = lpfHz,
            });

            if (voiceState is byte vs && !await session.SendVoiceStateAsync(vs))
                ConsoleFormatter.Warn("[SRX] --voice-state 未下发（无 CMD 特征）");
            ConsoleFormatter.Info("[SRX-AMP] 等待音频流（物理触发：K2 语音键 或 S1 扩音档）…");

            var clock = Stopwatch.StartNew();
            while (!cts.IsCancellationRequested)
            {
                try { await Task.Delay(2000, cts.Token); }
                catch (OperationCanceledException) { break; }
                var s = _passthrough.GetStats();
                var avgGap = Interlocked.Read(ref arrCount) > 0
                    ? Interlocked.Read(ref arrSumMs) / (double)Interlocked.Read(ref arrCount) : 0;
                ConsoleFormatter.Info(
                    $"[SRX-AMP] {clock.Elapsed.TotalSeconds:F0}s 泵入={fedFrames} 拒={rejectedFrames} " +
                    $"环深={s.RingDepthMs:F0}ms 估计延迟={s.EstimatedLatencyMs:F0}ms 到达均隙={avgGap:F1}ms");
            }
            // 停止前快照：StopAsync 拆链后活量统计失真（同 amp 命令口径）
            liveStats = _passthrough.GetStats();
        }
        catch (InvalidOperationException ex)
        {
            ConsoleFormatter.Error(ex.Message);
            return 1;
        }
        finally
        {
            await session.StopAsync();
            await _passthrough.StopAsync();
            await _central.DisconnectAsync();
            ConsoleFormatter.Info("已断开");
        }

        var stats = liveStats ?? _passthrough.GetStats();
        var maxGap = Interlocked.Read(ref arrMaxMs);
        var avgGapFinal = Interlocked.Read(ref arrCount) > 0
            ? Interlocked.Read(ref arrSumMs) / (double)Interlocked.Read(ref arrCount) : 0;
        ConsoleFormatter.Success($"扩音收尾：泵入 {fedFrames} 帧（{fedFrames * 7.5 / 1000:F1}s 音频），拒绝 {rejectedFrames}");
        ConsoleFormatter.Info(
            $"[SRX-AMP] 到达节奏：均隙 {avgGapFinal:F1}ms / 最大隙 {maxGap}ms（音频净产率 7.5ms/帧；" +
            "最大隙≫7.5ms=通知成批到达，Windows BLE 缓冲实证）");
        ConsoleFormatter.Info($"[SRX] {session.Stats.Summary()}");
        ConsoleFormatter.Info(
            $"[SRX-AMP] Box 段估计延迟 {stats.EstimatedLatencyMs:F0}ms（外源泵 0 + 处理链 + 环深活值 + 渲染缓冲 {stats.RenderLatencyMs}ms）；" +
            $"环深 avg {stats.RingDepthAvgMs:F1}ms / max {stats.RingDepthMaxMs:F1}ms；欠载 {stats.UnderrunEvents}");
        return fedFrames > 0 ? 0 : 2;
    }

    /// <summary>
    /// `srx probe`：链路质量实测（chirp 注入 + 解码流网格扫描 + 到达走表剖面）。
    /// 扬声器播 750ms 周期 chirp 训练列 → 笔端麦拾取 → BLE 回传 → 解码流上跑
    /// ChirpScanAnalyzer（形状/PPM 口径）+ StreamPacingAnalyzer（传输/缓冲活剖面）。
    /// 口径声明：绝对延迟含起点相位常量偏置（笔端自由跑钟，无共享锚），
    /// 同场内形状/PPM 有效、跨场对账勿比绝对值（对齐 t7/03 号口径注）；
    /// 绝对全链锚 = v2 议题（需真实语音流+人工标定或固件回执时间戳）。
    /// 运行前提：语音流必须在流（K2/S1 物理触发）。
    /// </summary>
    public async Task<int> ProbeAsync(string? mac, string? name, int sec, string? outPath, float volume)
    {
        var macDisplay = await ResolveTargetAsync(mac, name);
        if (macDisplay == null)
            return 1;

        var session = new SoftReceiverSession(_central, msg => ConsoleFormatter.Info($"[SRX] {msg}"));

        using var cts = new CancellationTokenSource();
        if (sec > 0)
            cts.CancelAfter(TimeSpan.FromSeconds(sec));
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var probe = new SoftReceiverProbe(session);
        try
        {
            await session.ConnectAsync(macDisplay);

            // 发射半边：默认渲染设备播 chirp 训练列（16k mono → 端点混音格式）
            probe.Start(volume);
            ConsoleFormatter.Info(
                $"[SRX-PROBE] chirp 训练列已开播 → [{probe.RenderDeviceName}]（750ms 周期；音量 {volume:F2}x 系统音量另计）；" +
                $"收流 {sec}s（语音流须在流：K2/S1 物理触发）…");

            await session.StartAsync(cts.Token);
        }
        catch (InvalidOperationException ex)
        {
            ConsoleFormatter.Error(ex.Message);
            return 1;
        }
        catch (OperationCanceledException)
        {
            // 正常收尾
        }
        catch (Exception ex)
        {
            // 播放链/音频端点异常兜底（WasapiOut 初始化 COM 异常等）——工具不裸崩
            ConsoleFormatter.Error($"probe 异常中断：{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            probe.Abort();
            await session.StopAsync();
            await _central.DisconnectAsync();
            ConsoleFormatter.Info("已断开");
        }

        // === 分析（连接已断，采集数据在探针内；CLI 与 Desktop 共用 SoftReceiverProbe） ===
        var stats = session.Stats;
        ConsoleFormatter.Info($"[SRX] {stats.Summary()}");
        var report = probe.StopAndAnalyze(outPath ?? $"srx_probe_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
        if (report == null)
        {
            ConsoleFormatter.Warn(
                $"解码音频帧不足（{stats.AudioFramesDecoded}）：语音流未在流（K2/S1 物理触发）——probe 需要真实上行才能测");
            return 2;
        }

        ConsoleFormatter.Info(
            $"[SRX-PROBE] 走表剖面：步长 {report.Pacing.FittedStepMs:F3}ms（PPM {report.Pacing.Ppm:F0}）" +
            $"偏差 p50 {report.Pacing.P50DevMs:F1}ms / p95 {report.Pacing.P95DevMs:F1}ms / max {report.Pacing.MaxDevMs:F1}ms" +
            $"（{report.Pacing.Frames} 帧；BLE+Windows 传输/缓冲活剖面，max=最深积压水位）");
        ConsoleFormatter.Info(
            $"[SRX-PROBE] chirp 检出 {report.Scan.DetectedCount}/{report.Scan.ExpectedCount}（缺失 {report.Scan.MissingSlots.Count} 槽）" +
            $"PPM {report.Scan.Ppm:F0}（形状口径；绝对延迟含起点相位常量偏置——笔端自由跑钟无共享锚，跨场勿比绝对值）");
        foreach (var hit in report.Scan.Hits.Take(5))
            ConsoleFormatter.Info($"  槽 {hit.Slot}: 峰位 {hit.PeakPos} 质量 {hit.Quality:F2}");

        ConsoleFormatter.Info($"[SRX-PROBE] 解码流 WAV → {report.WavPath}（{report.TotalPcmBytes / 32000.0:F1}s @16k mono；" +
                              "发射时刻自 playT0 起第 k·750ms——离线可做绝对锚 v2 标定）");
        return report.Scan.DetectedCount > 0 ? 0 : 3;
    }

    /// <summary>`srx batt`：查电量（0x41 下行 + RSP 应答，验软件接收器形态的命令/应答链）。</summary>
    public async Task<int> BattAsync(string? mac, string? name, int timeoutMs)
    {
        var macDisplay = await ResolveTargetAsync(mac, name);
        if (macDisplay == null)
            return 1;

        var session = new SoftReceiverSession(_central, msg => ConsoleFormatter.Info($"[SRX] {msg}"));
        var rspTcs = new TaskCompletionSource<SoftReceiverRspEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await session.ConnectAsync(macDisplay);
            session.ResponseReceived += (_, r) => rspTcs.TrySetResult(r);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (!await session.QueryBatteryAsync())
            {
                ConsoleFormatter.Error("无 CMD 特征，无法下发 0x41");
                return 1;
            }

            var rsp = await rspTcs.Task.WaitAsync(cts.Token);
            var batt = new VpResponse { CmdId = rsp.CmdId, Status = rsp.Status, Data = rsp.Data }.Battery;
            if (batt is byte pct)
                ConsoleFormatter.Success($"电量应答：{pct}%（cmd=0x{rsp.CmdId:X2} status={rsp.Status}）");
            else
                ConsoleFormatter.Warn($"应答非电量或非 OK：cmd=0x{rsp.CmdId:X2} status={rsp.Status} data={Convert.ToHexString(rsp.Data)}");
            return 0;
        }
        catch (OperationCanceledException)
        {
            ConsoleFormatter.Error("5s 内无 RSP 应答（在册预期之一：0x41 笔端未实现，issues-pencil/15——PC 查询电量=Dongle 缓存非笔端）");
            return 2;
        }
        catch (InvalidOperationException ex)
        {
            ConsoleFormatter.Error(ex.Message);
            return 1;
        }
        finally
        {
            await _central.DisconnectAsync();
            ConsoleFormatter.Info("已断开");
        }
    }

    /// <summary>目标解析：--mac 直接用；否则扫描 5s 按 SunVote 笔端识别推荐（复用 ble 域口径）。</summary>
    private async Task<string?> ResolveTargetAsync(string? mac, string? name)
    {
        if (!string.IsNullOrWhiteSpace(mac))
            return mac;

        ConsoleFormatter.Info("[SRX] 未给 --mac：扫描 5s 找笔端…");
        var devices = await _scanner.ScanAsync(5);
        var hit = string.IsNullOrWhiteSpace(name)
            ? devices.Where(d => (d.LocalName ?? "").StartsWith(BleNameConstants.PenPrefix, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(d => d.Rssi).FirstOrDefault()
            : devices.FirstOrDefault(d => (d.LocalName ?? "").Contains(name, StringComparison.OrdinalIgnoreCase));

        if (hit == null)
        {
            ConsoleFormatter.Error("扫描未找到笔端（键盘连着基站时不广播；或用 srx connect --mac <地址>）");
            return null;
        }
        ConsoleFormatter.Info($"[SRX] 目标：{hit.MacDisplayText} \"{hit.LocalName}\" rssi={hit.Rssi}dBm");
        return hit.MacDisplayText;
    }
}
