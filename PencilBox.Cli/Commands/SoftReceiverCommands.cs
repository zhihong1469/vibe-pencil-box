// <copyright file="SoftReceiverCommands.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using NAudio.Wave;
using PencilBox.Cli.Output;
using PencilBox.Core.Ble;
using PencilBox.Core.Ble.SoftReceiver;
using PencilBox.Core.Communication;

namespace PencilBox.Cli.Commands;

/// <summary>
/// 软件接收器命令面（形态③：PC 直连键盘收语音，绕过硬件基站）。
/// `srx scan` 找广播中的笔端 → `srx connect` 建连+svr1 核表 → `srx receive` 收流解码落 WAV
/// → `srx batt` 查电量验下行/应答链。会话编排/解析/解码在 Core（<see cref="SoftReceiverSession"/>），
/// 本类只做目标解析、WAV 落盘与文案。
/// 背景：链路上"基站蓄水池+USB"段的延迟冗余可由直连绕过（净收益以 chirp A/B 实测为准）；
/// 机制文档 = _share/box/docs/GitFlow/feature/soft-receiver/README.md。
/// </summary>
public class SoftReceiverCommands
{
    private readonly IBleCentral _central;
    private readonly IBleScanner _scanner;

    public SoftReceiverCommands(IBleCentral central, IBleScanner scanner)
    {
        _central = central;
        _scanner = scanner;
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
                ConsoleFormatter.Warn("零音频帧：键盘未进语音模式（识别/扩音）或该模式无上行，可试 --voice-state 1/2");
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
        catch (TimeoutException)
        {
            ConsoleFormatter.Error("5s 内无 RSP 应答（笔端未应答 0x41——量产裁剪或链路未通，属已知预期形态之一）");
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
