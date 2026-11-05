// <copyright file="SoftReceiverCommands.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Diagnostics;
using System.Globalization;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using PencilBox.Cli.Output;
using PencilBox.Core.Ble;
using PencilBox.Core.Ble.SoftReceiver;
using PencilBox.Core.Communication;
using PencilBox.Core.Dsp;
using PencilBox.Core.Models;
using PencilBox.Core.Protocol;
using PencilBox.Core.Services;
using PencilBox.Infrastructure.Audio;
using PencilBox.Infrastructure.Hid;
using PencilBox.Infrastructure.Platform;

namespace PencilBox.Cli.Commands;

/// <summary>
/// 软件接收器命令面（形态③：PC 直连键盘收语音，绕过硬件基站）。
/// `srx scan` 找广播中的笔端 → `srx connect` 建连+svr1 核表 → `srx receive` 收流解码落 WAV
/// → `srx amp` 解码 PCM 泵进扩音引擎实时出声 → `srx batt` 查电量验下行/应答链
/// → `srx hid` 基站 EP4 HID 直送 mSBC（08 号方案，绕 UAC 引擎）。
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
    private readonly IHidTransport _hidTransport;
    private ExclusiveRenderEngine? _exclusiveRender;

    public SoftReceiverCommands(IBleCentral central, IBleScanner scanner, IAudioPassthrough passthrough,
        IHidTransport hidTransport)
    {
        _central = central;
        _scanner = scanner;
        _passthrough = passthrough;
        _hidTransport = hidTransport;
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
    /// 软件键鼠层（--actions）：委托 <see cref="SrxKeyActionExecutor"/>（与 GUI 开关同一份逻辑）。
    /// 键盘腿=Keymap 出厂表驱动，鼠标腿=空鼠槽直通；事件与执行结果逐条落日志。
    /// </summary>
    private static void AttachKeyActions(SoftReceiverSession session)
    {
        var executor = new SrxKeyActionExecutor(
            msg => ConsoleFormatter.Info($"[SRX] {msg}"),
            msg => ConsoleFormatter.Warn($"[SRX] {msg}"));
        ConsoleFormatter.Info(
            "[SRX] 软件键鼠已启用（Keymap 出厂表驱动）：K3/K4=翻页 K5/K6=音量 K2=F13/F14 会话边界 S1=F15/F16；" +
            "空鼠=相对移动/左右键/滚轮直注（原始数据透传，无虚拟设备）");
        session.KeyAction += (_, a) => executor.HandleKeyAction(a);
        session.SwitchChanged += (_, s) => executor.HandleSwitch(s.Mode);
        session.MouseFrame += (_, m) => executor.HandleMouseFrame(m);
        session.MouseButton += (_, m) => executor.HandleMouseButton(m.Left, m.Right);
    }

    /// <summary>
    /// `srx receive`：收流解码落 WAV。语音须键盘自己在识别/扩音模式（或 --voice-state 触发），
    /// Ctrl+C 或 --sec 到点收尾出统计。--actions 开键动作层（0x13 语义键→系统注入）。
    /// </summary>
    public async Task<int> ReceiveAsync(string? mac, string? name, string? outPath, int sec,
        byte? voiceState, int timeoutMs, bool actions = false)
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
                if (actions)
                    AttachKeyActions(session);

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
        float volume, int latencyMs, bool aec, bool ans, bool afc, int hpfHz, int lpfHz,
        bool actions = false)
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
        if (actions)
            AttachKeyActions(session);

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

        PrintProbeReport(report, "BLE");
        return report.Scan.DetectedCount > 0 ? 0 : 3;
    }

    /// <summary>probe 报告统一打印（BLE/HID 两腿同格式；Desktop VM 另有平齐文案防跨程序集耦合）。</summary>
    private static void PrintProbeReport(SoftReceiverProbeReport report, string leg)
    {
        ConsoleFormatter.Info(
            $"[SRX-PROBE][{leg}] 走表剖面：步长 {report.Pacing.FittedStepMs:F3}ms（PPM {report.Pacing.Ppm:F0}）" +
            $"偏差 p50 {report.Pacing.P50DevMs:F1}ms / p95 {report.Pacing.P95DevMs:F1}ms / max {report.Pacing.MaxDevMs:F1}ms" +
            $"（{report.Pacing.Frames} 帧；腿+Windows 传输/缓冲活剖面，max=最深积压水位）");
        ConsoleFormatter.Info(
            $"[SRX-PROBE][{leg}] chirp 检出 {report.Scan.DetectedCount}/{report.Scan.ExpectedCount}（缺失 {report.Scan.MissingSlots.Count} 槽）" +
            $"PPM {report.Scan.Ppm:F0}（形状口径；绝对延迟含起点相位常量偏置——笔端自由跑钟无共享锚，跨场勿比绝对值）");
        foreach (var hit in report.Scan.Hits.Take(5))
            ConsoleFormatter.Info($"  槽 {hit.Slot}: 峰位 {hit.PeakPos} 质量 {hit.Quality:F2}");

        ConsoleFormatter.Info($"[SRX-PROBE][{leg}] 解码流 WAV → {report.WavPath}（{report.TotalPcmBytes / 32000.0:F1}s @16k mono；" +
                              "发射时刻自 playT0 起第 k·750ms——离线可做绝对锚 v2 标定）");
    }

    /// <summary>
    /// `srx hid`：基站 EP4 HID 直送 mSBC 通道（08 号方案，实验号 0x56）。
    /// 免驱 UAC 路被砍的替代轨：mSBC 60B 帧经 custom HID 64B 定长直传 PC，本地解码
    /// （u16 帧计数缺口插静音），预期砍掉设备蓄水池+UAC 引擎税（−50~90ms，实测为准）。
    /// 无 --device：列出基站 EP4 通道（★）即退；给 --device <名称/路径子串> 后：
    /// 连接 → --route on 切入 HID 直送（E1 OK 应答即读线程消费活证明）→ 收流
    /// --listen 秒（0=Ctrl+C）→ 收尾自动切回 UAC（--keep-route 保持，仅排障用）。
    /// --record <路径> 同时落 16k/16bit/mono WAV（解码 PCM，含补的静音帧）。
    /// --speaker 开外源泵扩音（--volume/--latency/--no-aec/--no-ans，与 srx amp 同链：
    /// 解码 PCM → AEC/ANS → 环形 → 扬声器）——A/B 实测主腿。
    /// --probe <秒> 延迟实测 HID 腿（2026-11-05 双腿化）：必须配 --route on（否则音频走 UAC、
    /// EP4 无帧）；chirp 开播 + 挂解码帧采集（SoftReceiverProbe HID 构造），收秒后出
    /// 走表+chirp 扫描报告（与 srx probe 同格式，轨标签 [HID]）；与 --speaker/--record 互斥。
    /// 注意：本固件须烧 test/hid 构建（DG_AUD_HID_ENABLE），否则 STATUS=0xFF 明示无能力。
    /// </summary>
    public async Task<int> HidAsync(string? device, string? route, int listenSec,
        string? recordPath, bool keepRoute, int timeoutMs,
        bool speaker = false, float volume = 1.0f, int latencyMs = 40,
        bool noAec = false, bool noAns = false, bool exclusive = false,
        int probeSec = 0)
    {
        // 1) 枚举 + 选台（只认 0xFF02 EP4 命令通道；device CLI 同款 ★ 识别）
        var devices = await _hidTransport.EnumerateDevicesAsync();
        var ep4s = devices.Where(d => d.IsCommandChannel).ToList();
        ConsoleFormatter.Info($"[SRX-HID] HID 设备 {devices.Count} 台，其中基站 EP4 命令通道（0xFF02 无 RID）{ep4s.Count} 台：");
        foreach (var d in ep4s)
            ConsoleFormatter.Info($"  ★ {d.ProductName}  {TailPath(d.DevicePath)}");
        if (ep4s.Count == 0)
        {
            ConsoleFormatter.Warn("未发现基站 EP4 通道（基站未插/驱动未拉起？device scan 看全表）");
            return 1;
        }

        HidDeviceInfo? target;
        if (string.IsNullOrWhiteSpace(device))
        {
            ConsoleFormatter.Info("提示：--device <名称/路径子串> 选台切入（无参只列台不动作）");
            return 0;
        }
        var hits = ep4s.Where(d =>
            d.ProductName.Contains(device, StringComparison.OrdinalIgnoreCase) ||
            d.DevicePath.Contains(device, StringComparison.OrdinalIgnoreCase) ||
            d.DevicePath.Contains(device.Replace("#", "\\"), StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 0)
        {
            ConsoleFormatter.Error($"\"{device}\" 无匹配 EP4 通道（按名称或路径子串匹配）");
            return 1;
        }
        if (hits.Count > 1)
        {
            ConsoleFormatter.Error($"\"{device}\" 命中 {hits.Count} 台，禁止静默取第一台：\n  " +
                string.Join("\n  ", hits.Select(d => d.ProductName + "  " + TailPath(d.DevicePath))));
            return 1;
        }
        target = hits[0];

        // 2) 连接 + 会话
        if (!await _hidTransport.ConnectAsync(target.DevicePath))
        {
            ConsoleFormatter.Error($"连接 EP4 失败：{target.ProductName}");
            return 1;
        }
        if (!_hidTransport.IsNoReportIdDevice)
        {
            ConsoleFormatter.Error("连接的不是 0xFF02 无 RID 承载（识别异常），退出");
            await _hidTransport.DisconnectAsync();
            return 1;
        }
        ConsoleFormatter.Success($"已连接 EP4：{target.ProductName}（{TailPath(target.DevicePath)}）");

        var session = new SrxHidSession(_hidTransport, msg => ConsoleFormatter.Info(msg));
        var routeIssued = false;

        // --probe 前置校验（连接后早退，免白连一趟）
        if (probeSec > 0)
        {
            if (route != "on")
            {
                ConsoleFormatter.Error("--probe 必须配 --route on：路由不在 HID 时音频走 UAC，EP4 通道无帧可测");
                await _hidTransport.DisconnectAsync();
                return 1;
            }
            if (speaker || recordPath != null)
            {
                ConsoleFormatter.Error("--probe 与 --speaker/--record 互斥（chirp 实测期间扩音须关/录制由 probe 自带 WAV 留档）");
                await _hidTransport.DisconnectAsync();
                return 1;
            }
        }
        try
        {
            session.WatchdogFallback += (_, _) =>
                ConsoleFormatter.Warn("[SRX-HID] 已回 UAC：后续音频报告将停止（重连后重试 route on）");

            // 3) 路由（on=切入 HID / off=切回 UAC / 缺省=不路由只听）
            if (route == "on")
            {
                if (!await session.RouteToHidAsync(timeoutMs))
                {
                    ConsoleFormatter.Error("切入 HID 直送失败（见上行应答原因）——UAC 路不受影响");
                    return 1;
                }
                routeIssued = true;
            }
            else if (route == "off")
            {
                if (!await session.RouteToUacAsync(timeoutMs))
                {
                    ConsoleFormatter.Error("切回 UAC 失败（无应答/拒绝）——基站可能仍在 HID 路或已自行回退");
                    return 1;
                }
            }
            else if (route != null)
            {
                ConsoleFormatter.Error($"--route 只认 on|off，收到 \"{route}\"");
                return 1;
            }

            // 3.5) --probe 延迟实测腿（与常规收流互斥；完成后走外层 finally 切回 UAC+断开）
            if (probeSec > 0)
            {
                var probe = new SoftReceiverProbe(session);
                using var pcts = new CancellationTokenSource();
                pcts.CancelAfter(TimeSpan.FromSeconds(probeSec));
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; pcts.Cancel(); };
                SoftReceiverProbeReport? report = null;
                try
                {
                    probe.Start(volume);
                    ConsoleFormatter.Info(
                        $"[SRX-PROBE][HID] chirp 训练列已开播 → [{probe.RenderDeviceName}]（750ms 周期；音量 {volume:F2}x）；" +
                        $"收流 {probeSec}s（语音流须在流：K2/S1 物理触发）…");
                    await Task.Delay(TimeSpan.FromSeconds(probeSec), pcts.Token);
                }
                catch (OperationCanceledException)
                {
                    ConsoleFormatter.Warn("[SRX-PROBE][HID] 已提前中止");
                }
                finally
                {
                    try { report = probe.StopAndAnalyze($"srx_probe_hid_{DateTime.Now:yyyyMMdd_HHmmss}.wav"); }
                    catch (Exception ex) { ConsoleFormatter.Error($"[SRX-PROBE][HID] 收尾异常：{ex.Message}"); }
                }
                if (report == null)
                {
                    ConsoleFormatter.Warn("[SRX-PROBE][HID] 解码音频帧不足：语音流未在流（K2/S1 物理触发）——HID 腿同样需要真实上行才能测");
                    return 2;
                }
                PrintProbeReport(report, "HID");
                return report.Scan.DetectedCount > 0 ? 0 : 3;
            }

            // 4) 统一消费漏斗：计数 + 录制（可选）+ 扩音泵（可选，与 srx amp 同一条外源链）
            var waveFormat = new WaveFormat(MsbcDecoder.SampleRate, 16, 1);
            long frameCount = 0, fedFrames = 0, rejectedFrames = 0, arrLast = 0, arrSumMs = 0, arrCount = 0, arrMaxMs = 0;
            using WaveFileWriter? writer = recordPath != null ? new WaveFileWriter(recordPath, waveFormat) : null;
            var writeLock = new object();
            var feedLock = new object();
            session.AudioFrameReady += (_, f) =>
            {
                Interlocked.Increment(ref frameCount);
                if (writer != null)
                    lock (writeLock)
                        writer.Write(f.Pcm, 0, f.Pcm.Length);
                if (!speaker)
                    return;
                lock (feedLock)
                {
                    long now = Environment.TickCount64;
                    if (arrLast != 0)
                    {
                        var gap = now - arrLast;
                        arrSumMs += gap;
                        arrCount++;
                        if (gap > Interlocked.Read(ref arrMaxMs)) Interlocked.Exchange(ref arrMaxMs, gap);
                    }
                    arrLast = now;
                    // 独占引擎 WritePcm 无拒绝语义（队列丢最旧）；共享泵按写入数判拒绝
                    var written = _exclusiveRender != null
                        ? f.Pcm.Length
                        : _passthrough.WriteExternalPcm(f.Pcm);
                    if (written == f.Pcm.Length)
                        fedFrames++;
                    else
                        rejectedFrames++;
                }
            };

            if (speaker)
            {
                if (exclusive)
                {
                    // 独占渲染（M3）：端点独占格式直出（吃满 Windows 音频引擎税）；
                    // 无 AEC/ANS 处理链=纯渲染延迟 A/B 腿；失败自动回退共享并留台账
                    _exclusiveRender = new ExclusiveRenderEngine();
                    using (var enumerator = new MMDeviceEnumerator())
                    {
                        var render = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                        _exclusiveRender.Start(render, volume, Math.Min(latencyMs, 20));
                    }
                    var rep = _exclusiveRender.GetReport();
                    ConsoleFormatter.Info(
                        $"[SRX-HID] 独占渲染：[{rep.DeviceName}] {rep.FormatText}" +
                        (rep.Exclusive ? "" : $"——回退原因：{rep.FallbackReason}（链路先通、引擎税后收）") +
                        "；独占=独占端点，期间本机其他声音被顶掉；无 AEC/ANS（纯渲染 A/B 腿）");
                }
                else
                {
                    await _passthrough.StartAsync(new PassthroughStartOptions
                    {
                        ExternalSource = true,
                        RenderLatencyMs = latencyMs,
                        Volume = volume,
                        EnableAec = !noAec,
                        EnableAns = !noAns,
                        EnableAfc = false,
                        HpfHz = 80,
                    });
                    ConsoleFormatter.Info(
                        $"[SRX-HID] 外源泵扩音已开：音量 {volume:F2}x，渲染缓冲 {latencyMs}ms，" +
                        $"处理链 AEC={(noAec ? 0 : 1)} ANS={(noAns ? 0 : 1)}（解码 PCM→处理链→扬声器；" +
                        "对 A/B：同场 route on/off 来回切，听感+Box 段估计延迟对比）");
                }
            }

            using var cts = new CancellationTokenSource();
            if (listenSec > 0)
                cts.CancelAfter(TimeSpan.FromSeconds(listenSec));
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            await ListenAsync(session, () => Interlocked.Read(ref frameCount), cts, listenSec, speaker);

            ConsoleFormatter.Success($"收流完成：解码帧 {Interlocked.Read(ref frameCount)}（{Interlocked.Read(ref frameCount) * 7.5 / 1000:F1}s 音频 @7.5ms/帧）");
            if (writer != null)
                ConsoleFormatter.Info($"[SRX-HID] WAV → {Path.GetFullPath(recordPath!)}");
            if (speaker)
            {
                if (_exclusiveRender != null)
                {
                    var rep = _exclusiveRender.GetReport();
                    ConsoleFormatter.Info(
                        $"[SRX-HID] 独占渲染收尾：[{rep.DeviceName}] {rep.FormatText}；欠载 {rep.Underruns}" +
                        (rep.FallbackReason != null ? $"；回退原因：{rep.FallbackReason}" : ""));
                    _exclusiveRender.Dispose();
                    _exclusiveRender = null;
                }
                else
                {
                    var stats = _passthrough.GetStats();
                    var avgGap = Interlocked.Read(ref arrCount) > 0
                        ? Interlocked.Read(ref arrSumMs) / (double)Interlocked.Read(ref arrCount) : 0;
                    ConsoleFormatter.Info(
                        $"[SRX-HID] 泵出：泵入 {fedFrames} 帧（{fedFrames * 7.5 / 1000:F1}s）拒绝 {rejectedFrames}；" +
                        $"到达均隙 {avgGap:F1}ms / 最大隙 {Interlocked.Read(ref arrMaxMs)}ms（≫7.5=报告成批到达）");
                    ConsoleFormatter.Info(
                        $"[SRX-HID] Box 段估计延迟 {stats.EstimatedLatencyMs:F0}ms（处理链+环深活值+渲染缓冲 {stats.RenderLatencyMs}ms）；" +
                        "A/B 判据=与 UAC 路（免驱直通）同场听感+串口戳对账，净差=HID 直送收益");
                }
            }
        }
        finally
        {
            // 5) 收尾：除非 --keep-route，切回 UAC 默认路（OTA/免驱常态不受影响）
            if (routeIssued && !keepRoute)
            {
                if (await session.RouteToUacAsync(timeoutMs))
                    ConsoleFormatter.Info("[SRX-HID] 已切回 UAC 默认路");
                else
                    ConsoleFormatter.Warn("[SRX-HID] 切回 UAC 无应答——若基站仍直送，拔插恢复；OTA 会话启动会自动 route=0 兜底");
            }
            if (speaker)
            {
                _exclusiveRender?.Dispose();
                _exclusiveRender = null;
                await _passthrough.StopAsync();
            }
            session.Dispose();
            await _hidTransport.DisconnectAsync();
            ConsoleFormatter.Info("[SRX-HID] " + session.Summary());
            ConsoleFormatter.Info("已断开 EP4");
        }
        return 0;
    }

    /// <summary>hid 收流主等待：心跳 2s 出实时计数（listenSec=0 走 Ctrl+C；speaker 时附 Box 段延迟）。</summary>
    private async Task ListenAsync(SrxHidSession session, Func<long> getFrameCount,
        CancellationTokenSource cts, int listenSec, bool speaker)
    {
        ConsoleFormatter.Info($"[SRX-HID] 收流中（{(listenSec > 0 ? $"{listenSec}s 后" : "Ctrl+C")}收尾）…");
        var clock = Stopwatch.StartNew();
        try
        {
            while (!cts.IsCancellationRequested)
            {
                try { await Task.Delay(2000, cts.Token); }
                catch (OperationCanceledException) { break; }
                var ch = session.ChannelStats;
                var latencyNote = _exclusiveRender != null
                    ? $"；[{_exclusiveRender.FormatText}] 欠载 {_exclusiveRender.Underruns}"
                    : speaker ? $"；估计延迟 {_passthrough.GetStats().EstimatedLatencyMs:F0}ms" : "";
                ConsoleFormatter.Info(
                    $"[SRX-HID] {clock.Elapsed.TotalSeconds:F0}s 解码帧 {getFrameCount()}{latencyNote}；{ch}");
            }
        }
        catch (OperationCanceledException) { /* Ctrl+C 正常收尾 */ }
    }

    /// <summary>设备路径尾巴（\??\USB#VID_2F70&PID_EA11#...#{guid} → 中段可读部分）。</summary>
    private static string TailPath(string devicePath)
    {
        var s = devicePath.Replace("\\??\\", "");
        var hash = s.IndexOf('#');
        var tail = hash >= 0 ? s[(hash + 1)..] : s;
        var end = tail.IndexOf("#{");
        return end >= 0 ? tail[..end] : tail;
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

    /// <summary>
    /// `srx bridge`：虚拟麦克风桥（R1PBleAudio 对外腿，桥式路线——适配既有虚拟声卡，免驱动签名）。
    /// 常规：连接键盘收解码 PCM → 泵进虚拟声卡渲染端（如 VB-Cable "CABLE Input"），
    /// 第三方软件从同卡采集端（"CABLE Output"）当麦克风录音；延迟增量≈两端 WASAPI 缓冲（15~50ms 可调），
    /// 不进主链（识别/扩音/测量走进程内直供零新增）。
    /// --selftest：无键盘自主回环——chirp 训练列进桥渲染端，同卡采集端回采 → ChirpScanAnalyzer 对拍
    /// （检出率/PPM），验"桥形态+采样率链路"，无需语音流、无需键盘在位。
    /// </summary>
    public async Task<int> BridgeAsync(string? mac, string? name, int sec, string? device, float volume, bool selftest)
    {
        // 1) 找桥目标（虚拟声卡渲染端）
        NAudio.CoreAudioApi.MMDevice? render = null;
        NAudio.CoreAudioApi.MMDevice? capture = null;
        using (var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator())
        {
            var renders = enumerator.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active).ToList();
            var captures = enumerator.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.Active).ToList();
            if (!string.IsNullOrWhiteSpace(device))
            {
                render = renders.FirstOrDefault(d => d.FriendlyName.Contains(device, StringComparison.OrdinalIgnoreCase));
                if (render == null)
                {
                    ConsoleFormatter.Error($"--device \"{device}\" 无匹配渲染设备。现有：\n  " +
                        string.Join("\n  ", renders.Select(d => d.FriendlyName)));
                    return 1;
                }
                // 手指渲染端时按同族括注自动配采集端（物理声学回环/同卡配对均可）
                capture = captures.FirstOrDefault(d => SrxBridgePump.NameCandidatesMatch(render.FriendlyName, d.FriendlyName));
            }
            else
            {
                var (ri, ci) = SrxBridgePump.SelectPair(
                    renders.Select(d => d.FriendlyName).ToList(), captures.Select(d => d.FriendlyName).ToList());
                if (ri < 0)
                {
                    ConsoleFormatter.Error("未发现虚拟声卡配对端点（CABLE Input/Output 类）。现有渲染设备：\n  " +
                        string.Join("\n  ", renders.Select(d => d.FriendlyName)) +
                        "\n现有采集设备：\n  " + string.Join("\n  ", captures.Select(d => d.FriendlyName)) +
                        "\n安装 VB-Cable（免费）后重试，或用 --device <名字片段> 手指渲染设备");
                    return 1;
                }
                render = renders[ri];
                capture = captures[ci];
            }
        }

        using var renderDev = render;
        using var captureDev = capture;
        ConsoleFormatter.Info($"[SRX-BRIDGE] 渲染端（泵入）[{render.FriendlyName}]；采集端（第三方录音口）" +
            $"{(capture != null ? $"[{capture.FriendlyName}]" : "（--device 指定模式，未配对，自测不可用）")}");
        if (SrxBridgePump.IsUuShellName(render.FriendlyName) ||
            (capture != null && SrxBridgePump.IsUuShellName(capture.FriendlyName)))
        {
            ConsoleFormatter.Warn("[SRX-BRIDGE] 桥目标=UU远程外壳（第三方软件装的虚拟声卡，依赖其客户端转发，非 PencilBox 设备）——" +
                "正式对外请安装 VB-Cable（免费），装完重跑本命令会自动改选 CABLE Input");
        }

        // 2a) 自测：chirp 进桥 → 同卡回采 → 对拍
        if (selftest)
        {
            if (captureDev == null)
            {
                ConsoleFormatter.Error("自测需配对采集端（--device 单指定渲染端不可用）；装 VB-Cable 后免参重试");
                return 1;
            }
            return RunBridgeSelftest(renderDev, captureDev, Math.Clamp(sec, 5, 60), volume);
        }

        // 2b) 常规：键盘解码 PCM → 桥
        var macDisplay = await ResolveTargetAsync(mac, name);
        if (macDisplay == null)
            return 1;

        var session = new SoftReceiverSession(_central, msg => ConsoleFormatter.Info($"[SRX] {msg}"));
        var pump = new SrxBridgePump();
        using var cts = new CancellationTokenSource();
        if (sec > 0)
            cts.CancelAfter(TimeSpan.FromSeconds(sec));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        session.AudioFrameReady += (_, f) => pump.WritePcm(f.Pcm);

        try
        {
            await session.ConnectAsync(macDisplay);
            pump.Start(renderDev, volume);
            ConsoleFormatter.Info(
                $"[SRX-BRIDGE] 已开播：键盘解码 PCM → [{render.FriendlyName}]；" +
                $"第三方软件选麦克风「{(captureDev != null ? captureDev.FriendlyName : render.FriendlyName + " 对应采集端")}」即可收 R1 人声（延迟增量≈两端缓冲 15~50ms）");
            ConsoleFormatter.Info($"[SRX-BRIDGE] 泵流 {sec}s（语音流须在流：K2/S1 物理触发）…");

            var hb = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(2000, cts.Token);
                    ConsoleFormatter.Info($"[SRX-BRIDGE] 环深 {pump.RingDepthMs:F0}ms 欠载块 {pump.UnderrunBlocks} 已泵 {pump.PumpedSamples / 16000.0:F1}s 音频");
                }
            }, CancellationToken.None);
            _ = hb.ContinueWith(_ => { }, TaskScheduler.Default);

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
            ConsoleFormatter.Error($"bridge 异常中断：{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            pump.Dispose();
            await session.StopAsync();
            await _central.DisconnectAsync();
            ConsoleFormatter.Info("已断开");
        }

        ConsoleFormatter.Info($"[SRX] {session.Stats.Summary()}");
        if (pump.PumpedSamples == 0)
        {
            ConsoleFormatter.Warn("泵入 0 帧：语音流未在流（K2/S1 物理触发）——桥形态已通，等真实上行即可对外供音");
            return 2;
        }
        ConsoleFormatter.Success(
            $"桥收尾：泵入 {pump.PumpedSamples / 16000.0:F1}s 音频（{pump.PumpedSamples} 样本）欠载块 {pump.UnderrunBlocks}——" +
            "第三方软件从该采集端录音即 R1 麦克风形态");
        return 0;
    }

    /// <summary>桥自测：chirp 训练列→渲染端→同卡采集端回采→16k 重采样→网格对拍（无键盘自主回环）。</summary>
    private static int RunBridgeSelftest(NAudio.CoreAudioApi.MMDevice render, NAudio.CoreAudioApi.MMDevice capture, int sec, float volume)
    {
        var pump = new SrxBridgePump();
        var capBuffer = new MemoryStream();
        long capBytes = 0;
        NAudio.CoreAudioApi.WasapiCapture? recorder = null;
        try
        {
            pump.Start(render, volume);

            // 发射：chirp 训练列 → PCM16 → 环（Stopwatch 精确实时节拍——
            // Sleep(100)/100ms 块慢于实时会欠载拉长网格，首测 PPM 81781 实证）
            var chirp = new ChirpTrainSource();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(sec));
            var gen = Task.Run(() =>
            {
                var buf = new float[160]; // 10ms @16k
                var sw = System.Diagnostics.Stopwatch.StartNew();
                long fed = 0;
                var total = (long)sec * MsbcDecoder.SampleRate;
                while (!cts.IsCancellationRequested && fed < total)
                {
                    var target = Math.Min(total, (long)(sw.Elapsed.TotalSeconds * MsbcDecoder.SampleRate));
                    while (fed < target)
                    {
                        var n = chirp.Read(buf, 0, (int)Math.Min(buf.Length, target - fed));
                        var bytes = new byte[n * 2];
                        for (var i = 0; i < n; i++)
                        {
                            var s = (short)Math.Clamp(buf[i] * volume * 32767f, short.MinValue, short.MaxValue);
                            bytes[i * 2] = (byte)s;
                            bytes[(i * 2) + 1] = (byte)(s >> 8);
                        }
                        pump.WritePcm(bytes);
                        fed += n;
                    }
                    Thread.Sleep(5);
                }
            }, CancellationToken.None);

            // 回采：同卡采集端
            var capFormat = capture.AudioClient.MixFormat;
            recorder = new NAudio.CoreAudioApi.WasapiCapture(capture) { WaveFormat = capFormat };
            recorder.DataAvailable += (_, e) =>
            {
                capBuffer.Write(e.Buffer, 0, e.BytesRecorded);
                Interlocked.Add(ref capBytes, e.BytesRecorded);
            };
            recorder.StartRecording();
            ConsoleFormatter.Info($"[SRX-BRIDGE-SELFTEST] chirp 进 [{render.FriendlyName}] {sec}s，回采 [{capture.FriendlyName}]（{capFormat.SampleRate}Hz/{capFormat.Channels}ch/{capFormat.Encoding}）…");
            gen.Wait(CancellationToken.None);
            recorder.StopRecording();
            Thread.Sleep(200);

            // 混音格式 → mono short @16k（整数比盒式平均抽取）
            var ratio = capFormat.SampleRate / 16000;
            if (capFormat.SampleRate != ratio * 16000)
            {
                ConsoleFormatter.Warn($"回采率 {capFormat.SampleRate}Hz 非 16k 整数倍，跳过对拍（回采 {capBytes / 1000.0:F0}kB 已证链路通）");
                return 3;
            }
            var mono = SrxBridgePump.ExtractMonoShort(capBuffer.ToArray(), capFormat);
            var sig16k = SrxBridgePump.DecimateTo16k(mono, ratio);
            var rms = Math.Sqrt(sig16k.Average(x => (double)x * x));
            ConsoleFormatter.Info($"[SRX-BRIDGE-SELFTEST] 回采 {capBytes / 1000.0:F0}kB → mono {mono.Length} 样本 → 16k {sig16k.Length} 样本（RMS {rms:F0}/32767）；chirp 泵入 {pump.PumpedSamples / 16000.0:F1}s");

            var scan = ChirpScanAnalyzer.ScanByGrid(sig16k, MsbcDecoder.SampleRate, ChirpTemplate.PeriodSamples);
            ConsoleFormatter.Info(
                $"[SRX-BRIDGE-SELFTEST] 对拍：检出 {scan.DetectedCount}/{scan.ExpectedCount}（缺失 {scan.MissingSlots.Count} 槽）PPM {scan.Ppm:F0}" +
                "（形状口径；绝对延迟=渲染端缓冲+采集端缓冲，含起点常量偏置）");
            return scan.DetectedCount > 0 ? 0 : 3;
        }
        catch (Exception ex)
        {
            ConsoleFormatter.Error($"selftest 异常：{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            try { recorder?.StopRecording(); } catch { /* 收尾容错 */ }
            recorder?.Dispose();
            pump.Dispose();
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
