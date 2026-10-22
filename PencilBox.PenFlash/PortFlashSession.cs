// <copyright file="PortFlashSession.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.IO.Ports;
using System.Text;

namespace PencilBox.PenFlash;

/// <summary>流水线阶段（UI 状态列；01 号 P0-3：进 ISP → 烧录 → 校验 → 起跑）。</summary>
public enum FlashPhase
{
    Pending,
    EnterIsp,
    Burn,
    Run,
    CleanReset,
    Done
}

/// <summary>
/// 单口烧录会话（并发的最小单元，每口一个实例）：进 ISP → 拷固件（每口唯一名）→
/// 烧录+校验 → --run → 干净复位 → PASS/FAIL。流程逐条复刻 flash_r1p.ps1：
/// 其中「--run 后必补一记 DTR/RTS 干净复位」是 UART1 聋锁防线（01 号 §2.3-2，
/// --run 是 ISP bootloader 内跳转非芯片复位，ROM 残留态会让烧后串口轨字节级聋）。
/// </summary>
public sealed class PortFlashSession
{
    private readonly PenFlashEngine _engine;

    public string Port { get; }
    public string FirmwarePath { get; }

    public FlashPhase Phase { get; private set; } = FlashPhase.Pending;

    /// <summary>终态：true=PASS。null=尚未结束。</summary>
    public bool? Passed { get; private set; }

    /// <summary>终态摘要（FAIL 时含错误码中文处置）。</summary>
    public string Summary { get; private set; } = "";

    public event EventHandler<string>? Log;
    public event EventHandler? PhaseChanged;

    public PortFlashSession(PenFlashEngine engine, string port, string firmwarePath)
    {
        _engine = engine;
        Port = port;
        FirmwarePath = firmwarePath;
    }

    public async Task<bool> RunAsync(CancellationToken ct)
    {
        try
        {
            // 1) 进 ISP（BOOT 低 → RST 低 100ms → RST 释放 → BOOT 释放）
            SetPhase(FlashPhase.EnterIsp);
            LogLine($"[{Port}] 进 ISP（RST←RTS 反相、BOOT←DTR 反相）…");
            await SwitchModeAsync(enterIsp: true, ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            // 2) 拷固件进工具目录（OmBleIsp 在工具目录内按裸文件名烧；每口唯一名防并发写冲突）
            var copyName = PenFlashCore.UniqueFirmwareCopyName(Port);
            var copyPath = Path.Combine(_engine.ToolDir, copyName);
            await Task.Run(() => File.Copy(FirmwarePath, copyPath, overwrite: true), ct).ConfigureAwait(false);
            LogLine($"[{Port}] 固件已拷入工具目录：{copyName}");

            // 3) 烧录 + 校验（--verify 必带，UI 不提供关闭入口——量产漏检防线）
            SetPhase(FlashPhase.Burn);
            LogLine($"[{Port}] OmBleIsp 烧录+校验中（{PenFlashCore.ConvertOmPort(Port)} @ {PenFlashCore.IspBaudrate}）…");
            var (rc, output) = await _engine.RunOmBleIspAsync(
                $"--serial {PenFlashCore.ConvertOmPort(Port)} --baudrate {PenFlashCore.IspBaudrate} " +
                $"--chip-select 0 --app 0 --app-file {copyName} --verify", ct).ConfigureAwait(false);
            foreach (var line in output.Split('\n'))
                if (!PenFlashCore.IsProgressNoise(line.TrimEnd('\r')) && line.Trim().Length > 0)
                    LogLine($"  {line.TrimEnd('\r')}");
            LogLine($"[{Port}] OmBleIsp 退出码：{rc}");
            if (rc != 0)
                LogLine($"[{Port}] 处置：{PenFlashCore.DescribeErrorCode(rc)}");

            // 4) --run 起跑 + 5) 必补 DTR/RTS 干净复位（两步都不省）
            SetPhase(FlashPhase.Run);
            var (rcRun, _) = await _engine.RunOmBleIspAsync(
                $"--run --serial {PenFlashCore.ConvertOmPort(Port)} --baudrate {PenFlashCore.IspBaudrate} " +
                "--chip-select 0", ct).ConfigureAwait(false);
            if (rcRun != 0)
                LogLine($"[{Port}] [WARN] --run 失败（退出码 {rcRun}），改用 DTR/RTS 复位起跑");

            SetPhase(FlashPhase.CleanReset);
            LogLine($"[{Port}] 干净复位（清 ISP 残留态，UART1 聋锁修复）…");
            await SwitchModeAsync(enterIsp: false, ct).ConfigureAwait(false);

            // 6) 判定（退出码 0 且输出含 Finished Code:0，二者缺一不可——01 号 §2.2）
            SetPhase(FlashPhase.Done);
            Passed = PenFlashCore.IsPass(rc, output);
            Summary = Passed.Value
                ? $"PASS（Finished Code:0，已起跑）"
                : $"FAIL（退出码 {rc}）{PenFlashCore.DescribeErrorCode(rc)}";
            LogLine($"[{Port}] {(Passed.Value ? "[PASS]" : "[FAIL]")} {Summary}");
            return Passed.Value;
        }
        catch (OperationCanceledException)
        {
            Passed = false;
            Summary = "已取消（人工中止）";
            SetPhase(FlashPhase.Done);
            LogLine($"[{Port}] [FAIL] 人工取消");
            return false;
        }
        catch (Exception ex)
        {
            Passed = false;
            Summary = $"FAIL（{ex.Message}）";
            SetPhase(FlashPhase.Done);
            LogLine($"[{Port}] [FAIL] 异常：{ex.Message}");
            return false;
        }
    }

    /// <summary>DTR/RTS 电平切换（ps1 Invoke-ModeSwitch 逐行复刻；net48 同步 SerialPort，跑在线程池）。</summary>
    private async Task SwitchModeAsync(bool enterIsp, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            using var ser = new SerialPort(Port, PenFlashCore.IspBaudrate)
            {
                ReadTimeout = 200,
                WriteTimeout = 200,
            };
            ser.Open();
            // 初始：两脚释放（RST 高、BOOT 高 = 正常运行态）
            ser.RtsEnable = false;   // RTS 低 → RST 高
            ser.DtrEnable = false;   // DTR 低 → BOOT 高
            Thread.Sleep(PenFlashCore.TSettleMs);

            if (enterIsp)
            {
                // BOOT 低 → RST 低 100ms → RST 释放（BOOT 仍低）→ BOOT 释放
                ser.DtrEnable = true;
                Thread.Sleep(PenFlashCore.TSettleMs);
                ser.RtsEnable = true;
                Thread.Sleep(PenFlashCore.TResetMs);
                ser.RtsEnable = false;
                Thread.Sleep(PenFlashCore.TSettleMs);
                ser.DtrEnable = false;
            }
            else
            {
                // BOOT 高 → RST 低 100ms → RST 释放 → BOOT 保持高 0.5s
                ser.DtrEnable = false;
                Thread.Sleep(PenFlashCore.TSettleMs);
                ser.RtsEnable = true;
                Thread.Sleep(PenFlashCore.TResetMs);
                ser.RtsEnable = false;
                Thread.Sleep(PenFlashCore.TBootHoldMs);
            }
            ser.Close();
        }, ct).ConfigureAwait(false);
    }

    private void SetPhase(FlashPhase phase)
    {
        Phase = phase;
        PhaseChanged?.Invoke(this, EventArgs.Empty);
    }

    private void LogLine(string line) => Log?.Invoke(this, line);
}

/// <summary>烧后抽检（01 号 P1-9）：同口开 1M 波特率监听 N 秒抓开机横幅（ps1 -Listen 同款）。</summary>
public static class PostFlashProbe
{
    /// <summary>监听串口日志 seconds 秒，返回收到的文本（UTF8 解码，与 ps1 一致）。</summary>
    public static Task<string> ListenAsync(string port, int seconds, CancellationToken ct) => Task.Run(() =>
    {
        var sb = new StringBuilder();
        try
        {
            using var ser = new SerialPort(port, PenFlashCore.MonitorBaudrate) { ReadTimeout = 200 };
            ser.Open();
            ser.DiscardInBuffer();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < seconds && !ct.IsCancellationRequested)
            {
                try
                {
                    var n = ser.BytesToRead;
                    if (n > 0)
                    {
                        var buf = new byte[n];
                        _ = ser.Read(buf, 0, n);
                        sb.Append(Encoding.UTF8.GetString(buf));
                    }
                    else
                    {
                        Thread.Sleep(20);
                    }
                }
                catch { break; }
            }
            ser.Close();
        }
        catch (Exception)
        {
            // 口被占/未插——抽检是可选复核，失败仅返回已捕获内容
        }
        return sb.ToString();
    }, ct);

    /// <summary>从开机横幅文本抓版本 token（如 v0.13.21），抓不到返回 null。</summary>
    public static string? ExtractBannerVersion(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(
            text, @"v[0-9]+\.[0-9]+\.[0-9]+(?:[-.][0-9A-Za-z]+)*");
        return m.Success ? m.Value : null;
    }
}
