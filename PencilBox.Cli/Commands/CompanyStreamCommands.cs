// <copyright file="CompanyStreamCommands.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Text;
using PencilBox.Cli.Output;
using PencilBox.Core.Ble;
using PencilBox.Core.Communication;
using PencilBox.Core.Configuration;
using PencilBox.Core.Fsm;
using PencilBox.Core.Protocol;
using PencilBox.Core.Protocol.Company;
using PencilBox.Infrastructure.Serial;

namespace PencilBox.Cli.Commands;

/// <summary>
/// `company stream` / `company record` —— `0x62` 空鼠遥测流命令面（笔端 06 卷；**仅串口轨**，
/// 与 `0x61` 同族：`srv_f5_cmds` 就地应答、流帧无应答 PC 不回帧）。
///
/// ⚠️ 应答/流帧同 CMDTYPE `0x62`，判别口径=**载荷长**（≤3B=START/STOP 应答，=21B=流帧）——
/// 故 START/STOP 不走通用 <see cref="CommandRequester"/>（它只按 CMDTYPE 匹配，会误吞流帧当应答），
/// 本类内置"子命令回显 + 载荷长"双校验的专用等待。流帧计数/丢流/CSV 全在
/// <see cref="TelemetryRecorder"/>/<see cref="TelemetryCsv"/>（与 Desktop 录波同一单点）。
/// </summary>
public class CompanyStreamCommands
{
    private readonly ISerialTransport _serialTransport;
    private readonly SerialCommandChannel _serialChannel;
    private readonly ICompanyCodec _codec;
    private readonly IClock _clock;
    private readonly CompanyConfig _config;

    public CompanyStreamCommands(
        ISerialTransport serialTransport,
        SerialCommandChannel serialChannel,
        ICompanyCodec codec,
        IClock clock,
        CompanyConfig config)
    {
        _serialTransport = serialTransport;
        _serialChannel = serialChannel;
        _codec = codec;
        _clock = clock;
        _config = config;
    }

    // ============ stream on / off ============

    /// <summary>
    /// `stream on [--div N] [--secs N] [--keep]`：START → 实测 N 秒帧率（缺省 5s）→ 打印；
    /// 缺省**测完自动 STOP**（CLI 每命令自建自拆，不留后台流），`--keep` 才保持开（供配合 serial log 抓流）。
    /// </summary>
    public async Task<int> StreamOnAsync(string? port, byte divider, int measureSecs, bool keep)
    {
        if (!await EnsureSerialAsync(port))
            return 1;

        ConsoleFormatter.Info($"[STREAM] 0x62/01 START（div={divider}，1=每帧 50Hz）← {_serialChannel.Describe}");
        var start = await RequestStreamCmdAsync(_codec.EncodeStreamStart(divider), StreamCmd.Start);
        if (start == null)
            return NoAnswer(port);
        if (start.Value.Status != TuningStatus.Ok)
            return StatusFail("START", start.Value.Status);

        var recorder = new TelemetryRecorder();
        var unsub = SubscribeFrames(recorder);
        try
        {
            ConsoleFormatter.Info($"[STREAM] 实测 {measureSecs}s 帧率…（静置或正常使用笔端均可）");
            await Task.Delay(Math.Max(1, measureSecs) * 1000);

            double expected = 50.0 / divider;
            double fps = recorder.Fps;
            string verdict = fps >= expected * 0.9
                ? "达标（≥90% 理论值）"
                : "低于理论值 90%——笔端判据 3 未过（EVT 调度延迟抖动？），见笔端 06 卷 §5";
            if (fps >= expected * 0.9)
                ConsoleFormatter.Success($"[STREAM] 实测帧率 {fps:F1} fps（div={divider}，理论 {expected:F0}fps）——{verdict}");
            else
                ConsoleFormatter.Warn($"[STREAM] 实测帧率 {fps:F1} fps（div={divider}，理论 {expected:F0}fps）——{verdict}");

            if (keep)
            {
                ConsoleFormatter.Warn("[STREAM] --keep：保持推流中。注意：本进程退出口即关闭，笔端会继续向空口发流直至收到 STOP 或复位——尽快用 `stream off` 收尾");
                return 0;
            }

            var stop = await RequestStreamCmdAsync(_codec.EncodeStreamStop(), StreamCmd.Stop);
            if (stop == null)
            {
                ConsoleFormatter.Warn("STOP 无应答（流可能仍在推）——重试 `company stream off`（幂等）");
                return 1;
            }

            ConsoleFormatter.Info($"[STREAM] 0x62/00 STOP → STATUS=0x{stop.Value.Status:X2}（未开再停=00 幂等）");
            return 0;
        }
        finally
        {
            unsub();
        }
    }

    /// <summary>`stream off`：发 STOP（幂等；未开再停=00）</summary>
    public async Task<int> StreamOffAsync(string? port)
    {
        if (!await EnsureSerialAsync(port))
            return 1;

        var stop = await RequestStreamCmdAsync(_codec.EncodeStreamStop(), StreamCmd.Stop);
        if (stop == null)
            return NoAnswer(port);
        if (stop.Value.Status != TuningStatus.Ok)
            return StatusFail("STOP", stop.Value.Status);

        ConsoleFormatter.Success("[STREAM] 已 STOP（未开再停=00 幂等）");
        return 0;
    }

    // ============ record：START → 收 N 秒 → STOP → CSV ============

    /// <summary>
    /// `record [--secs N] [--div N] [--out f.csv]`：START → 收 N 秒（缺省 30s）→ STOP → CSV 落盘
    /// （列=pc_rx_ts,seq,flags,raw 六轴,rate 双轴,out 双口，**原码直出不换算**）；
    /// 尾注 `# meta: fps,dropped,div,fw`。Ctrl+C 提前收束：照常 STOP + 落盘（录到的部分不丢）。
    /// </summary>
    public async Task<int> RecordAsync(string? port, int secs, byte divider, string? outFile)
    {
        if (!await EnsureSerialAsync(port))
            return 1;

        // 0x03 读版本（容忍失败：meta 标 unknown）
        string? firmware = null;
        var verResp = await new CommandRequester(_clock, _config).RequestAsync(
            _serialChannel, _codec.EncodeReadVersion(), CompanyCmd.ReadVersion);
        if (verResp != null && _codec.TryParseVersionResp(verResp, out var ver) && ver.Status == CompanyStatus.Ok)
            firmware = $"0.{ver.SverMajor}.{ver.SverMinor}.{ver.SverPatch}";

        var recorder = new TelemetryRecorder();
        var unsub = SubscribeFrames(recorder);
        try
        {
            ConsoleFormatter.Info($"[STREAM] 0x62/01 START（div={divider}）← {_serialChannel.Describe}");
            var start = await RequestStreamCmdAsync(_codec.EncodeStreamStart(divider), StreamCmd.Start);
            if (start == null)
                return NoAnswer(port);
            if (start.Value.Status != TuningStatus.Ok)
                return StatusFail("START", start.Value.Status);

            using var cts = new CancellationTokenSource();
            ConsoleCancelEventHandler onBreak = (_, e) => { e.Cancel = true; cts.Cancel(); };
            Console.CancelKeyPress += onBreak;
            try
            {
                for (int elapsed = 1; elapsed <= secs; elapsed++)
                {
                    try { await Task.Delay(1000, cts.Token); }
                    catch (OperationCanceledException) { break; }

                    if (elapsed % 5 == 0 || elapsed == secs)
                        ConsoleFormatter.Info($"[STREAM] {elapsed}/{secs}s：已收 {recorder.Count} 帧（丢 {recorder.Dropped}）");
                }
            }
            finally
            {
                Console.CancelKeyPress -= onBreak;
            }
        }
        finally
        {
            unsub();
        }

        var stop = await RequestStreamCmdAsync(_codec.EncodeStreamStop(), StreamCmd.Stop);
        bool stopOk = stop != null && stop.Value.Status == TuningStatus.Ok;
        if (!stopOk)
            ConsoleFormatter.Warn("STOP 未确认（无应答或 STATUS≠00）——`company stream off` 幂等收尾");

        if (recorder.Count == 0)
        {
            ConsoleFormatter.Error("录制结束：0 帧——笔端未推流（确认空鼠已开、固件含 0x62；笔端 06 卷判据 1/3）");
            return 1;
        }

        var path = string.IsNullOrWhiteSpace(outFile)
            ? $"airmouse_record_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            : outFile.Trim();
        File.WriteAllText(path, TelemetryCsv.Build(recorder.Samples, recorder.Fps, recorder.Dropped, divider, firmware),
            new UTF8Encoding(false));

        double ratio = recorder.DroppedRatio;
        ConsoleFormatter.Success(
            $"[STREAM] 录制完成：{recorder.Count} 帧 @ {recorder.Fps:F1}fps（div={divider}，固件 {firmware ?? "unknown"}）");
        if (ratio < 0.001)
            ConsoleFormatter.Success($"[STREAM] 丢流 {recorder.Dropped}/{recorder.Count + recorder.Dropped}（{ratio * 100:F3}%）——判据 <0.1% 达标");
        else
            ConsoleFormatter.Warn($"[STREAM] 丢流 {recorder.Dropped}/{recorder.Count + recorder.Dropped}（{ratio * 100:F2}%）——判据 <0.1% 未达标（串口竞争/口被抢？）");
        ConsoleFormatter.Success($"CSV 已存：{Path.GetFullPath(path)}——指标：python tools/airmouse_metrics.py <该文件>；回放：空鼠工作台「打开录波」");

        return 0;
    }

    // ============ stillstats：零漂统计模式（对齐卷 06 §4.1，2026-10-15 用户需求） ============

    /// <summary>
    /// `stillstats [--secs 60] [--csv f.csv]`：笔**静置**开统计——收 N 秒流 → STOP → 直出零漂分布报告
    /// （双通道：raw=去偏差前全量零漂 / rate=补偿后残余；三方对照 0x07~0x0A；CSV 照落盘复用 record 格式）。
    /// 动了笔→动段被门剔除，剔除 >20% 提示重跑；0 帧=1。`--csv` 给离线模式：直接分析已有录波不出串口。
    /// </summary>
    public async Task<int> StillStatsAsync(string? port, int secs, byte divider, string? outFile,
        int stages, double gateDps, bool json, string? analyzeCsv)
    {
        // —— 离线模式：读 CSV → 同一单点分析（无串口；三方对照缺笔端实读=null） ——
        if (!string.IsNullOrWhiteSpace(analyzeCsv))
        {
            var rows = TelemetryCsv.ReadAll(analyzeCsv);
            if (rows.Count == 0)
            {
                ConsoleFormatter.Error($"离线分析：{analyzeCsv} 无样本（先 record/stillstats 产出 CSV）");
                return 1;
            }
            var offline = DriftSurvey.Analyze(
                rows.Select(r => (r.PcRxMs, r.Frame)).ToList(), new DriftSurvey.Options(gateDps, stages));
            PrintStillReport(offline, json, Path.GetFullPath(analyzeCsv));
            return offline.AcceptedFrames == 0 ? 1 : 0;
        }

        if (!await EnsureSerialAsync(port))
            return 1;

        // 三方对照前置：笔端实读 0x07/0x08（RAM 当前零偏）+ 0x09/0x0A（NVDS 持久值）；读不到留空不阻断
        var bias = await ReadBiasContextAsync();

        // 0x03 读版本（meta 尾注用；容忍失败）
        string? firmware = null;
        var verResp = await new CommandRequester(_clock, _config).RequestAsync(
            _serialChannel, _codec.EncodeReadVersion(), CompanyCmd.ReadVersion);
        if (verResp != null && _codec.TryParseVersionResp(verResp, out var ver) && ver.Status == CompanyStatus.Ok)
            firmware = $"0.{ver.SverMajor}.{ver.SverMinor}.{ver.SverPatch}";

        var recorder = new TelemetryRecorder();
        var unsub = SubscribeFrames(recorder);
        try
        {
            ConsoleFormatter.Info($"[STILL] 笔请静置（架上/平放）→ 0x62/01 START（div={divider}，统计 {secs}s，门 {gateDps:F2}dps）← {_serialChannel.Describe}");
            var start = await RequestStreamCmdAsync(_codec.EncodeStreamStart(divider), StreamCmd.Start);
            if (start == null)
                return NoAnswer(port);
            if (start.Value.Status != TuningStatus.Ok)
                return StatusFail("START", start.Value.Status);

            using var cts = new CancellationTokenSource();
            ConsoleCancelEventHandler onBreak = (_, e) => { e.Cancel = true; cts.Cancel(); };
            Console.CancelKeyPress += onBreak;
            try
            {
                for (int elapsed = 1; elapsed <= secs; elapsed++)
                {
                    try { await Task.Delay(1000, cts.Token); }
                    catch (OperationCanceledException) { break; }

                    if (elapsed % 10 == 0 || elapsed == secs)
                        ConsoleFormatter.Info($"[STILL] {elapsed}/{secs}s：已收 {recorder.Count} 帧");
                }
            }
            finally
            {
                Console.CancelKeyPress -= onBreak;
            }
        }
        finally
        {
            unsub();
        }

        var stop = await RequestStreamCmdAsync(_codec.EncodeStreamStop(), StreamCmd.Stop);
        if (stop == null || stop.Value.Status != TuningStatus.Ok)
            ConsoleFormatter.Warn("STOP 未确认（无应答或 STATUS≠00）——`company stream off` 幂等收尾");

        if (recorder.Count == 0)
        {
            ConsoleFormatter.Error("统计结束：0 帧——笔端未推流（0.13.24 已知欠口=发射未生效；见 pc-issues report/2026-10-15）");
            return 1;
        }

        // CSV 照落盘（复用 record 格式——metrics.py 温漂/漂移指标可对同一文件互校）
        var path = string.IsNullOrWhiteSpace(outFile)
            ? $"airmouse_still_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            : outFile.Trim();
        File.WriteAllText(path, TelemetryCsv.Build(recorder.Samples, recorder.Fps, recorder.Dropped, divider, firmware),
            new UTF8Encoding(false));

        var report = DriftSurvey.Analyze(
            recorder.Samples.Select(s => (s.PcRxMs, s.Frame)).ToList(),
            new DriftSurvey.Options(gateDps, stages), bias);
        PrintStillReport(report, json, Path.GetFullPath(path));
        return report.AcceptedFrames == 0 ? 1 : 0;
    }

    /// <summary>三方对照读数：0x07/0x08（RAM）+ 0x09/0x0A（NVDS）；单项读失败=null 不阻断统计</summary>
    private async Task<DriftSurvey.BiasContext> ReadBiasContextAsync()
    {
        async Task<float?> ReadOne(byte id)
        {
            var resp = await new CommandRequester(_clock, _config).RequestAsync(
                _serialChannel, _codec.EncodeTuningRead(id), CompanyCmd.TuningParam);
            if (resp != null && _codec.TryParseTuningReadResp(resp, out var status, out var echoId, out var value) &&
                status == TuningStatus.Ok && echoId == id)
                return value;
            return null;
        }

        return new DriftSurvey.BiasContext(
            await ReadOne(0x07), await ReadOne(0x08), await ReadOne(0x09), await ReadOne(0x0A));
    }

    /// <summary>零漂统计报告打印（对齐卷 06 §4.1：双通道表/三方对照/分阶段/温漂数/决策链；双单位 dps(定点原值)）</summary>
    private static void PrintStillReport(DriftSurvey.Report r, bool json, string csvPath)
    {
        if (json)
        {
            Console.Out.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                csv = csvPath,
                total_frames = r.TotalFrames,
                accepted = r.AcceptedFrames,
                rejected = r.RejectedFrames,
                reject_ratio = Math.Round(r.RejectRatio, 4),
                span_ms = r.SpanMs,
                temp_start_c = Math.Round(r.TempStartC, 2),
                temp_end_c = Math.Round(r.TempEndC, 2),
                temp_span_c = Math.Round(r.TempSpanC, 2),
                bias = r.Bias == null ? null : new
                {
                    ram_gx = r.Bias.RamGx, ram_gz = r.Bias.RamGz,
                    nvds_gx = r.Bias.NvdsGx, nvds_gz = r.Bias.NvdsGz,
                },
                gx = AxisDto(r.Gx), gz = AxisDto(r.Gz),
                rate_yaw = AxisDto(r.RateYaw), rate_roll = AxisDto(r.RateRoll),
                stages = r.Stages.Select(s => new
                {
                    index = s.Index, frames = s.Frames,
                    start_ms = s.StartMs, end_ms = s.EndMs,
                    gx_median_dps = Round4(s.Gx.MedianDps), gx_p5_dps = Round4(s.Gx.P5 / 100.0), gx_p95_dps = Round4(s.Gx.P95 / 100.0),
                    gz_median_dps = Round4(s.Gz.MedianDps), gz_p5_dps = Round4(s.Gz.P5 / 100.0), gz_p95_dps = Round4(s.Gz.P95 / 100.0),
                    rate_yaw_median_dps = Round4(s.RateYaw.MedianDps),
                    rate_roll_median_dps = Round4(s.RateRoll.MedianDps),
                }),
                temp_regression_valid = r.TempRegressionValid,
                temp_slope_gx_dps_per_c = r.TempRegressionValid ? (double?)Math.Round(r.TempSlopeGxDpsPerC, 5) : null,
                temp_slope_gz_dps_per_c = r.TempRegressionValid ? (double?)Math.Round(r.TempSlopeGzDpsPerC, 5) : null,
                residual_exceeds_guard = r.ResidualExceedsGuard,
            }, new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            return;
        }

        if (r.AcceptedFrames == 0)
        {
            ConsoleFormatter.Error($"零漂统计作废：{r.TotalFrames} 帧全被剔除（动了笔/冻结）——静置重跑（对齐卷 06 §4.1）");
            return;
        }

        ConsoleFormatter.Success($"[STILL] 统计窗 {r.SpanMs / 1000.0:F1}s：样本 {r.AcceptedFrames}/{r.TotalFrames}（剔除 {r.RejectedFrames}={r.RejectRatio * 100:F1}%）" +
                                 (r.RejectRatio > DriftSurvey.RejectRatioWarn ? "——剔除超 20%，统计窗被污染建议重跑" : "（<20% 门 ✓）"));

        if (r.Bias is { } b)
            ConsoleFormatter.Info($"[STILL] 三方对照：RAM 零偏 0x07/0x08 = {Fmt(b.RamGx)} / {Fmt(b.RamGz)} dps ｜ NVDS 0x09/0x0A = {Fmt(b.NvdsGx)} / {Fmt(b.NvdsGz)} dps" +
                                  (b.NvdsGx == null ? "（NVDS 读失败=可能未固化，0）" : ""));

        ConsoleFormatter.Info("[STILL] 分布统计（全窗；dps，括号=0.01dps 定点原值；raw=去偏差前全量零漂，rate=补偿后残余）");
        Console.Out.WriteLine("  通道      frames  min            max            mean           median         RMS            σ              P5             P95");
        foreach (var a in new[] { r.Gx, r.Gz, r.RateYaw, r.RateRoll })
        {
            if (a == null) continue;
            Console.Out.WriteLine(
                $"  {a.Channel,-9} {a.Count,6}  {Cell(a.Min)} {Cell(a.Max)} {Cell(a.Mean)} {Cell(a.Median)} {Cell(a.Rms)} {Cell(a.Sigma)} {Cell(a.P5)} {Cell(a.P95)}");
        }

        if (r.Stages.Count > 0)
        {
            ConsoleFormatter.Info($"[STILL] 分阶段（等样本数 {r.Stages.Count} 段；中位（P5~P95）dps——看 Kalman 收敛/温漂趋势）");
            foreach (var s in r.Stages)
                Console.Out.WriteLine(
                    $"  #{s.Index} {s.StartMs / 1000.0,6:F1}~{s.EndMs / 1000.0:F1}s {s.Frames,5} 帧  " +
                    $"gx {Fmt3(s.Gx.MedianDps)}({Fmt3(s.Gx.P5 / 100.0)}~{Fmt3(s.Gx.P95 / 100.0)})  " +
                    $"gz {Fmt3(s.Gz.MedianDps)}({Fmt3(s.Gz.P5 / 100.0)}~{Fmt3(s.Gz.P95 / 100.0)})  " +
                    $"yaw {Fmt3(s.RateYaw.MedianDps)}({Fmt3(s.RateYaw.P5 / 100.0)}~{Fmt3(s.RateYaw.P95 / 100.0)})  " +
                    $"roll {Fmt3(s.RateRoll.MedianDps)}({Fmt3(s.RateRoll.P5 / 100.0)}~{Fmt3(s.RateRoll.P95 / 100.0)})");
        }

        if (r.TempRegressionValid)
            ConsoleFormatter.Info($"[STILL] 温漂回归：温度 {r.TempStartC:F2}→{r.TempEndC:F2}°C（跨度 {r.TempSpanC:F2}）→ " +
                                  $"gx {r.TempSlopeGxDpsPerC:+0.00000;-0.00000} dps/°C ｜ gz {r.TempSlopeGzDpsPerC:+0.00000;-0.00000} dps/°C");
        else
            ConsoleFormatter.Info($"[STILL] 温漂回归：跳过（温度跨度 {r.TempSpanC:F2}°C < {DriftSurvey.MinTempSpanC:F1}——要联动温漂请录 ≥5min 且带冷热差）");

        if (r.ResidualExceedsGuard)
            ConsoleFormatter.Warn($"[STILL] 决策：残余零漂 |mean| > {DriftSurvey.GuardDeadzoneDps}dps（笔端守卫死区）——建议 `param set --id 0x0B --value 1` 固化当前 RAM 零偏，或等笔端守卫自动写");
        else
            ConsoleFormatter.Success($"[STILL] 决策：补偿后残余零漂 |mean| ≤ {DriftSurvey.GuardDeadzoneDps}dps 守卫死区——当前补偿覆盖良好");

        ConsoleFormatter.Success($"录波已存：{csvPath}——互校：python tools/airmouse_metrics.py <该文件>（漂移 RMS/温漂回归）");
    }

    private static object? AxisDto(DriftSurvey.AxisStats? a) => a == null ? null : new
    {
        channel = a.Channel, count = a.Count,
        min = a.Min, max = a.Max,
        mean = Round4(a.MeanDps), median = Round4(a.MedianDps),
        rms = Round4(a.Rms / 100.0), sigma = Round4(a.Sigma / 100.0),
        p5 = Round4(a.P5 / 100.0), p95 = Round4(a.P95 / 100.0),
    };   // dps 域（定点/100）

    private static double Round4(double v) => Math.Round(v, 4);

    /// <summary>定点原值 → dps（定点）双单位格：`-0.123(-12)`</summary>
    private static string Cell(double fixedPoint) =>
        $"{fixedPoint / 100.0,7:F3}({fixedPoint,5:0})";

    private static string Fmt(float? v) => v == null ? "—" : $"{(double)v:+0.000;-0.000;0.000}";

    private static string Fmt3(double v) => $"{v:+0.000;-0.000;0.000}";

    // ============ 私有 ============

    /// <summary>
    /// START/STOP 专用等待：**载荷 ≤3B + 子命令回显** 双校验（流帧 21B 不会误配对）；
    /// 先挂监听再发送，超时 500ms × MaxRetry 重发（同 CommandRequester 节奏）。
    /// 返回 null=无应答。
    /// </summary>
    private async Task<(byte Status, byte Divider)?> RequestStreamCmdAsync(byte[] request, byte expectSubCmd)
    {
        int timeout = _config.ResponseTimeoutMs;
        for (int attempt = 0; attempt <= _config.MaxRetry; attempt++)
        {
            var tcs = new TaskCompletionSource<(byte, byte)>(TaskCreationOptions.RunContinuationsAsynchronously);

            void Handler(object? sender, byte[] frame)
            {
                if (!CompanyFrame.TryParse(frame, out var baseCmd, out var type, out var data) ||
                    baseCmd != CompanyFrame.BaseCmdResponse || type != CompanyCmd.StreamTelemetry)
                    return;
                if (!_codec.TryParseStreamResp(data, expectSubCmd, out var status, out var div))
                    return;   // 流帧/异子命令应答：静默忽略
                tcs.TrySetResult((status, div));
            }

            _serialChannel.FrameReceived += Handler;
            try
            {
                await _serialChannel.SendAsync(request);
                var timeoutTask = Task.Delay(timeout);
                await Task.WhenAny(tcs.Task, timeoutTask);
                if (tcs.Task.IsCompleted)
                    return tcs.Task.Result;
            }
            finally
            {
                _serialChannel.FrameReceived -= Handler;
            }
        }

        return null;
    }

    /// <summary>订阅流帧 → 录制器（打点=Environment.TickCount64 单调毫秒；返回退订委托）</summary>
    private Action SubscribeFrames(TelemetryRecorder recorder)
    {
        void Handler(object? sender, byte[] frame)
        {
            if (!CompanyFrame.TryParse(frame, out var baseCmd, out var type, out var data) ||
                baseCmd != CompanyFrame.BaseCmdResponse || type != CompanyCmd.StreamTelemetry)
                return;
            if (_codec.TryParseTelemetryFrame(data, out var tf))
                recorder.Add(Environment.TickCount64, tf);
        }

        _serialChannel.FrameReceived += Handler;
        return () => _serialChannel.FrameReceived -= Handler;
    }

    /// <summary>STATUS≠00 归因（00/03 两态；01/02 不在 0x62 STATUS 语义内，原样露出）</summary>
    private static int StatusFail(string action, byte status)
    {
        if (status == TuningStatus.OtaBusy)
            ConsoleFormatter.Error($"0x62 {action} 被拒：OTA 升级会话进行中（笔端护串口带宽，升级完再测）");
        else
            ConsoleFormatter.Error($"0x62 {action} 失败：STATUS=0x{status:X2}");
        return 1;
    }

    private int NoAnswer(string? port)
    {
        ConsoleFormatter.Error(
            $"0x62 无应答（{port ?? "(未指定端口)"} 超时 {_config.ResponseTimeoutMs}ms×{_config.MaxRetry + 1} 次）：serial probe 试读确认笔端在位；" +
            "占用串口的程序先关；确认笔端固件已含 0x62 支持（笔端 06 卷）");
        return 1;
    }

    private async Task<bool> EnsureSerialAsync(string? port)
    {
        if (_serialChannel.IsOpen)
            return true;

        if (string.IsNullOrWhiteSpace(port))
        {
            ConsoleFormatter.Error("遥测流仅串口轨（PC↔笔端 UART1 直连，基站零感知）：请用 --port COMx 指定笔端串口");
            var ports = await _serialTransport.EnumeratePortsAsync();
            if (ports.Count > 0)
                ConsoleFormatter.Info("可用串口：" + string.Join(", ", ports.Select(p => p.PortName)));
            return false;
        }

        var settings = new SerialPortSettings { BaudRate = _config.SerialBaudRate };
        if (!await _serialTransport.ConnectAsync(port, settings))
        {
            ConsoleFormatter.Error($"打开串口失败：{port}（占用串口的程序先关——PencilBox.Desktop / sscom 教训；serial list 可列端口）");
            return false;
        }

        ConsoleFormatter.Info($"笔端命令通道就绪：{_serialChannel.Describe}");
        return true;
    }
}
