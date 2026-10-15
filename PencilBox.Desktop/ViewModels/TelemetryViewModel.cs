// <copyright file="TelemetryViewModel.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PencilBox.Core.Ble;
using PencilBox.Core.Communication;
using PencilBox.Core.Configuration;
using PencilBox.Core.Fsm;
using PencilBox.Core.Protocol;
using PencilBox.Core.Protocol.Company;
using PencilBox.Infrastructure.Serial;
using Microsoft.Win32;

namespace PencilBox.Desktop.ViewModels;

/// <summary>
/// `0x62` 遥测流与录波视图（pc-issues 对齐卷 06 §2/§3；空鼠工作台新增页）。
///
/// 通信与 CLI `company stream/record` 完全同源：同一 <see cref="ICompanyCodec"/> 编解码、
/// 同一 <see cref="TelemetryRecorder"/>/<see cref="TelemetryCsv"/> 单点（CLI 录的 CSV 本页直接回放，反之亦然）。
/// 三视图=原始时域（raw_gx/gz → dps）/ 输出时域（out_x/y + rate 可叠加）/ 输入-输出曲线
/// （|rate| 分桶 vs |out| 均值）；叠图诊断模式默认关（量纲打架，讨论稿结论）。
/// 另有**全量数据读出**面板：13 信号+seq/flags 逐行露出最新帧纯数值（图表只画判读通道，
/// gy/加速度三轴/rate_roll/flags 不画线但在此可见；温度字段 S18 待笔端裁决后补行）。
/// </summary>
public partial class TelemetryViewModel : ObservableObject
{
    private readonly ISerialTransport _serialTransport;
    private readonly SerialCommandChannel _serialChannel;
    private readonly ICompanyCodec _codec;
    private readonly CompanyConfig _config;
    private readonly DispatcherTimer _refreshTimer;

    /// <summary>环形样本缓冲（ts=接收毫秒；容量 600s@50Hz，超限丢头部——录波上限同此）——锁内读写</summary>
    private readonly object _gate = new();
    private readonly List<(long Ts, TelemetryFrame F)> _samples = new();
    private const int MaxSamples = 30000;

    /// <summary>回放模式：数据来自 CSV，禁止 START/录波（口未开也可见）</summary>
    private bool _replayMode;

    public TelemetryViewModel(ISerialTransport serialTransport, SerialCommandChannel serialChannel,
        ICompanyCodec codec, IClock clock, CompanyConfig config)
    {
        _serialTransport = serialTransport;
        _serialChannel = serialChannel;
        _codec = codec;
        _clock = clock;
        _config = config;

        _serialChannel.FrameReceived += OnFrameReceived;
        foreach (var r in new[] { _rSeq, _rFlags, _rFrozen, _rDrag, _rBias, _rNvds, _rTemp,
                 _rGx, _rGy, _rGz, _rAx, _rAy, _rAz, _rRateYaw, _rRateRoll, _rOutX, _rOutY })
            LiveRows.Add(r);
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _refreshTimer.Tick += (_, _) => RefreshCharts();
        _refreshTimer.Start();
    }

    private readonly IClock _clock;

    // ============ 状态 ============

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private string _previewButtonText = "开始预览";

    partial void OnIsStreamingChanged(bool value) => PreviewButtonText = value ? "停止（STOP）" : "开始预览";

    [ObservableProperty]
    private string _statusText = "未推流。连接（调参页）后可实时预览/录波；也可直接打开 CSV 回放。";

    [ObservableProperty]
    private string _statsText = "";

    [ObservableProperty]
    private int _divIndex;   // 0=1(50Hz) 1=2(25Hz) 2=4 3=5

    [ObservableProperty]
    private int _recordSecs = 30;

    [ObservableProperty]
    private bool _showRateOverlay;          // 输出图叠加 rate（默认关）

    [ObservableProperty]
    private bool _overlayDiag;              // 叠图诊断模式（默认关，量纲打架）

    public ObservableCollection<string> DivOptions { get; } = new() { "1（每帧 50Hz）", "2（25Hz）", "4（12.5Hz）", "5（10Hz）" };

    private byte Divider => DivIndex switch { 0 => 1, 1 => 2, 2 => 4, _ => 5 };

    // ============ 三视图系列 ============

    [ObservableProperty]
    private PointCollection _rawGxPoints = new();

    [ObservableProperty]
    private PointCollection _rawGzPoints = new();

    [ObservableProperty]
    private string _rawScaleText = "±10dps";

    [ObservableProperty]
    private PointCollection _outXPoints = new();

    [ObservableProperty]
    private PointCollection _outYPoints = new();

    [ObservableProperty]
    private PointCollection _rateOverlayPoints = new();

    [ObservableProperty]
    private PointCollection _diagOverlayPoints = new();

    [ObservableProperty]
    private PointCollection _ioPoints = new();

    [ObservableProperty]
    private string _ioScaleText = "";

    // ============ 全量数据读出（最新帧；与三视图同一刷新时钟，回放同源） ============

    /// <summary>读出面板一行（值仅在变化时触发绑定，空转零开销）</summary>
    public sealed class LiveValueRow : ObservableObject
    {
        private string _value = "—";
        public string Name { get; }
        public string Value { get => _value; set { if (_value != value) { _value = value; OnPropertyChanged(); } } }
        public LiveValueRow(string name) => Name = name;
    }

    private readonly LiveValueRow _rSeq = new("seq"), _rFlags = new("flags"),
        _rFrozen = new("冻结bit0"), _rDrag = new("拖拽bit1"), _rBias = new("零偏标定bit2"),
        _rNvds = new("NVDS载入bit3"), _rTemp = new("temp_imu"),
        _rGx = new("gx"), _rGy = new("gy"), _rGz = new("gz"),
        _rAx = new("ax"), _rAy = new("ay"), _rAz = new("az"),
        _rRateYaw = new("rate_yaw"), _rRateRoll = new("rate_roll"),
        _rOutX = new("out_x"), _rOutY = new("out_y");

    /// <summary>全量读出面板（17 行定名：seq/flags/四状态位/温度/六轴/rate×2/out×2）</summary>
    public ObservableCollection<LiveValueRow> LiveRows { get; } = new();

    private void UpdateLiveReadout(TelemetryFrame? f)
    {
        if (f == null)
        {
            foreach (var r in LiveRows)
                r.Value = "—";
            return;
        }
        _rSeq.Value = f.Seq.ToString();
        _rFlags.Value = $"0x{f.Flags:X2}";
        _rFrozen.Value = f.IsFrozen ? "●" : "○";
        _rDrag.Value = f.IsDragIntent ? "●" : "○";
        _rBias.Value = f.IsBiasCalibrated ? "●" : "○";
        _rNvds.Value = f.IsBiasFromNvds ? "●" : "○";
        _rTemp.Value = $"{f.TempC:F2} °C";
        _rGx.Value = $"{f.RawGx * 0.01:F2} dps";
        _rGy.Value = $"{f.RawGy * 0.01:F2} dps";
        _rGz.Value = $"{f.RawGz * 0.01:F2} dps";
        _rAx.Value = $"{f.RawAx * 0.001:F3} g";
        _rAy.Value = $"{f.RawAy * 0.001:F3} g";
        _rAz.Value = $"{f.RawAz * 0.001:F3} g";
        _rRateYaw.Value = $"{f.RateYaw * 0.01:F2} dps";
        _rRateRoll.Value = $"{f.RateRoll * 0.01:F2} dps";
        _rOutX.Value = ((int)f.OutX).ToString();
        _rOutY.Value = ((int)f.OutY).ToString();
    }

    // ============ 帧接收 ============

    private void OnFrameReceived(object? sender, byte[] frame)
    {
        if (!IsStreaming)
            return;   // 预览/录波外不收（含他人会话的帧，避免脏数据）

        if (!CompanyFrame.TryParse(frame, out var baseCmd, out var type, out var data) ||
            baseCmd != CompanyFrame.BaseCmdResponse || type != CompanyCmd.StreamTelemetry)
            return;
        if (!_codec.TryParseTelemetryFrame(data, out var tf))
            return;

        lock (_gate)
        {
            _samples.Add((Environment.TickCount64, tf));
            if (_samples.Count > MaxSamples)
                _samples.RemoveAt(0);
        }
    }

    // ============ 预览开关 / 录波 / 回放 ============

    [RelayCommand]
    private async Task TogglePreviewAsync()
    {
        if (IsStreaming)
        {
            await StopAsync("已 STOP（预览结束；窗口保留最近 60s 数据）");
            return;
        }

        if (!await StartAsync())
            return;

        _replayMode = false;
        IsStreaming = true;
        StatusText = $"实时预览中（div={Divider}）——滑条/录波可并行；帧率看右上角实测值";
    }

    [RelayCommand]
    private async Task RecordAsync()
    {
        if (_replayMode)
        {
            StatusText = "回放模式下不能录波——先清空回放数据";
            return;
        }

        if (!await StartAsync())
            return;

        IsStreaming = true;
        int secs = Math.Clamp(RecordSecs, 1, 600);
        StatusText = $"录波 {secs}s 中…（照常使用笔端：静止 3s → 慢圆 → 快甩 → 点目标）";
        long windowStart = Environment.TickCount64;
        long deadline = windowStart + secs * 1000L;
        while (Environment.TickCount64 < deadline)
        {
            await Task.Delay(200);
            lock (_gate)
                StatsText = $"{_samples.Count(s => s.Ts >= windowStart)} 帧";
        }
        await StopAsync(null);

        // 录波窗按时间戳截取（预览缓冲即数据源——同帧同源；缓冲 60s 足容 600s? 否：>60s 录波被环形丢弃）
        List<(long Ts, TelemetryFrame F)> window;
        lock (_gate)
            window = _samples.Where(s => s.Ts >= windowStart).ToList();
        if (window.Count == 0)
        {
            StatusText = "录波结束：0 帧——确认空鼠已开、笔端固件含 0x62";
            return;
        }

        // 0x03 版本（容忍失败：meta 标 unknown）
        string? firmware = null;
        try
        {
            var verResp = await new CommandRequester(_clock, _config).RequestAsync(
                _serialChannel, _codec.EncodeReadVersion(), CompanyCmd.ReadVersion);
            if (verResp != null && _codec.TryParseVersionResp(verResp, out var ver) && ver.Status == CompanyStatus.Ok)
                firmware = $"0.{ver.SverMajor}.{ver.SverMinor}.{ver.SverPatch}";
        }
        catch { /* 版本读不到不阻塞 */ }

        var dialog = new SaveFileDialog
        {
            Filter = "CSV 录波|*.csv",
            FileName = $"airmouse_record_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };
        if (dialog.ShowDialog() != true)
        {
            StatusText = "已取消保存（录到的数据仍在预览窗口）";
            return;
        }

        var dropped = CountDropped(window);
        File.WriteAllText(dialog.FileName,
            TelemetryCsv.Build(window.Select(s => (s.Ts, s.F)), FpsOf(window), dropped, Divider, firmware),
            new UTF8Encoding(false));
        double ratio = (double)dropped / Math.Max(1, window.Count + dropped);
        StatusText = $"录波已存：{dialog.FileName}（{window.Count} 帧 @ {FpsOf(window):F1}fps，丢流 {ratio * 100:F3}%）" +
                     "——metrics：python tools/airmouse_metrics.py";
    }

    // ============ 零漂统计模式（对齐卷 06 §4.1 stillsurvey；CLI stillstats 同一 DriftSurvey 单点） ============

    [ObservableProperty]
    private bool _isSurveyRunning;

    [ObservableProperty]
    private string _surveyButtonText = "开始零漂统计";

    partial void OnIsSurveyRunningChanged(bool value) => SurveyButtonText = value ? "停止统计" : "开始零漂统计";

    /// <summary>统计时长（秒；建议 ≥60，要联动温漂回归 ≥300 且带冷热差）</summary>
    [ObservableProperty]
    private int _surveySecs = 60;

    /// <summary>统计面板摘要（样本/剔除比/三方对照/温漂数/决策链，多行）</summary>
    [ObservableProperty]
    private string _surveySummary = "笔静置（架上/平放）后点「开始零漂统计」——双通道分布 + 三方对照 + 温漂数一次直出（06 卷 §4.1）；动笔会被自动剔除，剔除 >20% 提示重跑。";

    /// <summary>全窗分布统计行（四通道）</summary>
    public ObservableCollection<SurveyRow> SurveyRows { get; } = new();

    /// <summary>分阶段行（等样本数切窗；看 Kalman 收敛/温漂趋势）</summary>
    public ObservableCollection<SurveyStageRow> SurveyStages { get; } = new();

    /// <summary>零漂统计面板一行（双单位 dps(0.01dps 原值) 已格式化）</summary>
    public sealed class SurveyRow
    {
        public string Channel { get; init; } = "";
        public int Frames { get; init; }
        public string Min { get; init; } = "";
        public string Max { get; init; } = "";
        public string Mean { get; init; } = "";
        public string Median { get; init; } = "";
        public string Rms { get; init; } = "";
        public string Sigma { get; init; } = "";
        public string P5 { get; init; } = "";
        public string P95 { get; init; } = "";
    }

    /// <summary>分阶段一行：中位（P5~P95）dps</summary>
    public sealed class SurveyStageRow
    {
        public string Stage { get; init; } = "";
        public string Window { get; init; } = "";
        public int Frames { get; init; }
        public string Gx { get; init; } = "";
        public string Gz { get; init; } = "";
        public string Yaw { get; init; } = "";
        public string Roll { get; init; } = "";
    }

    private CancellationTokenSource? _surveyCts;

    /// <summary>
    /// 零漂统计开关：实时模式=静置收流 N 秒→STOP→CSV（可选保存）→报告直出；
    /// 回放模式=直接分析回放缓冲（同一录波重跑数值一致——确定性验收）。
    /// </summary>
    [RelayCommand]
    private async Task ToggleSurveyAsync()
    {
        if (IsSurveyRunning)
        {
            _surveyCts?.Cancel();
            return;
        }

        if (_replayMode)
        {
            List<(long Ts, TelemetryFrame F)> snap;
            lock (_gate)
                snap = _samples.ToList();
            FillSurveyReport(DriftSurvey.Analyze(snap.Select(s => (s.Ts, s.F)).ToList()),
                null, $"回放缓冲 {snap.Count} 帧（离线）");
            return;
        }

        if (!await StartAsync())
            return;

        IsStreaming = true;
        IsSurveyRunning = true;
        _surveyCts = new CancellationTokenSource();
        var ct = _surveyCts.Token;

        // 三方对照前置：0x07/0x08（RAM 当前零偏）+ 0x09/0x0A（NVDS 持久值）；读不到不阻断
        var bias = await ReadBiasContextAsync();

        int secs = Math.Clamp(SurveySecs, 10, 3600);
        StatusText = $"零漂统计 {secs}s 中…（笔静置；动笔会被门剔除）";
        long windowStart = Environment.TickCount64;
        long deadline = windowStart + secs * 1000L;
        bool aborted = false;
        while (Environment.TickCount64 < deadline)
        {
            try { await Task.Delay(200, ct); }
            catch (OperationCanceledException) { aborted = true; break; }
            lock (_gate)
                StatsText = $"{_samples.Count(s => s.Ts >= windowStart)} 帧";
        }

        await StopAsync(null);
        IsSurveyRunning = false;

        List<(long Ts, TelemetryFrame F)> window;
        lock (_gate)
            window = _samples.Where(s => s.Ts >= windowStart).ToList();
        if (window.Count == 0)
        {
            SurveySummary = "统计作废：0 帧——笔端未推流（0.13.24 已知欠口=发射未生效，见 report/2026-10-15）";
            return;
        }

        var report = DriftSurvey.Analyze(
            window.Select(s => (s.Ts, s.F)).ToList(), new DriftSurvey.Options(Stages: 4), bias);

        // CSV 照落盘（复用 record 格式；取消保存不挡报告）
        string? savedPath = null;
        var dialog = new SaveFileDialog
        {
            Filter = "CSV 录波|*.csv",
            FileName = $"airmouse_still_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                string? firmware = null;
                try
                {
                    var verResp = await new CommandRequester(_clock, _config).RequestAsync(
                        _serialChannel, _codec.EncodeReadVersion(), CompanyCmd.ReadVersion);
                    if (verResp != null && _codec.TryParseVersionResp(verResp, out var ver) && ver.Status == CompanyStatus.Ok)
                        firmware = $"0.{ver.SverMajor}.{ver.SverMinor}.{ver.SverPatch}";
                }
                catch { /* meta 标 unknown */ }

                File.WriteAllText(dialog.FileName,
                    TelemetryCsv.Build(window.Select(s => (s.Ts, s.F)), FpsOf(window),
                        CountDropped(window), Divider, firmware),
                    new UTF8Encoding(false));
                savedPath = dialog.FileName;
            }
            catch (Exception ex)
            {
                StatusText = $"CSV 保存失败：{ex.Message}";
            }
        }

        FillSurveyReport(report, savedPath,
            (aborted ? "（手动提前停止）" : null) ?? "");
    }

    /// <summary>三方对照读数：0x07/0x08/0x09/0x0A 单参读（失败=null 不阻断）</summary>
    private async Task<DriftSurvey.BiasContext?> ReadBiasContextAsync()
    {
        async Task<float?> ReadOne(byte id)
        {
            try
            {
                var resp = await new CommandRequester(_clock, _config).RequestAsync(
                    _serialChannel, _codec.EncodeTuningRead(id), CompanyCmd.TuningParam);
                if (resp != null && _codec.TryParseTuningReadResp(resp, out var status, out var echoId, out var value) &&
                    status == TuningStatus.Ok && echoId == id)
                    return value;
            }
            catch { /* 单项失败不阻断 */ }
            return null;
        }

        if (!_serialChannel.IsOpen)
            return null;
        return new DriftSurvey.BiasContext(
            await ReadOne(0x07), await ReadOne(0x08), await ReadOne(0x09), await ReadOne(0x0A));
    }

    /// <summary>报告 → 面板行 + 摘要（口径与 CLI stillstats 报告一致）</summary>
    private void FillSurveyReport(DriftSurvey.Report r, string? savedPath, string note)
    {
        SurveyRows.Clear();
        SurveyStages.Clear();
        if (r.AcceptedFrames == 0)
        {
            SurveySummary = $"统计作废：{r.TotalFrames} 帧全被剔除（动了笔/冻结）——静置重跑（06 卷 §4.1）";
            return;
        }

        foreach (var a in new[] { r.Gx, r.Gz, r.RateYaw, r.RateRoll })
        {
            if (a == null) continue;
            SurveyRows.Add(new SurveyRow
            {
                Channel = a.Channel,
                Frames = a.Count,
                Min = Cell(a.Min), Max = Cell(a.Max),
                Mean = Cell(a.Mean), Median = Cell(a.Median),
                Rms = Cell(a.Rms), Sigma = Cell(a.Sigma),
                P5 = Cell(a.P5), P95 = Cell(a.P95),
            });
        }

        foreach (var s in r.Stages)
        {
            SurveyStages.Add(new SurveyStageRow
            {
                Stage = $"#{s.Index}",
                Window = $"{s.StartMs / 1000.0:F1}~{s.EndMs / 1000.0:F1}s",
                Frames = s.Frames,
                Gx = Band(s.Gx), Gz = Band(s.Gz), Yaw = Band(s.RateYaw), Roll = Band(s.RateRoll),
            });
        }

        var lines = new List<string>
        {
            $"统计窗 {r.SpanMs / 1000.0:F1}s：样本 {r.AcceptedFrames}/{r.TotalFrames}（剔除 {r.RejectedFrames}={r.RejectRatio * 100:F1}%）" +
            (r.RejectRatio > DriftSurvey.RejectRatioWarn ? "——剔除超 20%，统计窗被污染建议重跑" : "（<20% 门 ✓）") + note,
        };
        if (r.Bias is { } b)
            lines.Add($"三方对照：RAM 零偏 0x07/0x08 = {Fmt(b.RamGx)} / {Fmt(b.RamGz)} dps ｜ NVDS 0x09/0x0A = {Fmt(b.NvdsGx)} / {Fmt(b.NvdsGz)} dps");
        lines.Add(r.TempRegressionValid
            ? $"温漂回归：温度 {r.TempStartC:F2}→{r.TempEndC:F2}°C → gx {r.TempSlopeGxDpsPerC:+0.00000;-0.00000} ｜ gz {r.TempSlopeGzDpsPerC:+0.00000;-0.00000} dps/°C"
            : $"温漂回归：跳过（温度跨度 {r.TempSpanC:F2}°C < {DriftSurvey.MinTempSpanC:F1}；要联动请录 ≥5min 且带冷热差）");
        lines.Add(r.ResidualExceedsGuard
            ? $"⚠ 残余零漂 |mean| > {DriftSurvey.GuardDeadzoneDps}dps 守卫死区——可写 0x0B=1 固化当前 RAM 零偏（调参页 0x0B 卡）或等笔端守卫自动写"
            : $"✓ 补偿后残余零漂 |mean| ≤ {DriftSurvey.GuardDeadzoneDps}dps 守卫死区——当前补偿覆盖良好");
        if (savedPath != null)
            lines.Add($"录波已存：{savedPath}");

        SurveySummary = string.Join("\n", lines);
    }

    private static string Cell(double fixedPoint) => $"{fixedPoint / 100.0:F3}({fixedPoint:0})";

    private static string Band(DriftSurvey.AxisStats a) =>
        $"{a.MedianDps:+0.000;-0.000;0.000}（{a.P5 / 100.0:+0.000;-0.000;0.000}~{a.P95 / 100.0:+0.000;-0.000;0.000}）";

    private static string Fmt(float? v) => v == null ? "—" : $"{(double)v:+0.000;-0.000;0.000}";

    [RelayCommand]
    private void OpenCsv()
    {
        var dialog = new OpenFileDialog { Filter = "CSV 录波|*.csv|所有文件|*.*" };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var rows = CsvFile.ReadAll(dialog.FileName);
            lock (_gate)
            {
                _samples.Clear();
                foreach (var (ts, f) in rows)
                {
                    _samples.Add((ts, f));
                    if (_samples.Count > MaxSamples)
                        _samples.RemoveAt(0);
                }
            }
            _replayMode = true;
            IsStreaming = false;
            StatusText = $"回放：{Path.GetFileName(dialog.FileName)}（{rows.Count} 帧，无笔也可复盘；清空后可重新实时）";
            RefreshCharts();
        }
        catch (Exception ex)
        {
            StatusText = $"CSV 解析失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void Clear()
    {
        lock (_gate)
            _samples.Clear();
        _replayMode = false;
        RefreshCharts();
        StatusText = "已清空（实时/回放均可重新开始）";
    }

    /// <summary>START（专用等待：载荷 ≤3B + 子命令回显，流帧 21B 不会误配对——CLI 同口径）</summary>
    private async Task<bool> StartAsync()
    {
        if (_replayMode)
        {
            StatusText = "回放模式下不能推流——先清空";
            return false;
        }
        if (!_serialChannel.IsOpen)
        {
            StatusText = "串口未开——先在调参页连接笔端串口";
            return false;
        }

        var resp = await RequestStreamCmdAsync(_codec.EncodeStreamStart(Divider), StreamCmd.Start);
        if (resp == null)
        {
            StatusText = "START 无应答——笔端 0x62 未实现前属预期（链路未断）";
            return false;
        }
        if (resp.Value.Status != TuningStatus.Ok)
        {
            StatusText = resp.Value.Status == TuningStatus.OtaBusy
                ? "START 被拒：OTA 升级会话进行中（笔端护串口带宽）"
                : $"START 失败：STATUS=0x{resp.Value.Status:X2}";
            return false;
        }
        return true;
    }

    private async Task StopAsync(string? doneText)
    {
        var resp = await RequestStreamCmdAsync(_codec.EncodeStreamStop(), StreamCmd.Stop);
        IsStreaming = false;
        if (resp == null)
            StatusText = "STOP 无应答（流可能仍在推）——重试停止或重连";
        else
            StatusText = doneText ?? "已 STOP";
    }

    /// <summary>START/STOP 专用等待（子命令回显 + 载荷长判别；500ms×4，CLI 同口径）</summary>
    private async Task<(byte Status, byte Divider)?> RequestStreamCmdAsync(byte[] request, byte expectSubCmd)
    {
        for (int attempt = 0; attempt <= _config.MaxRetry; attempt++)
        {
            var tcs = new TaskCompletionSource<(byte, byte)>(TaskCreationOptions.RunContinuationsAsynchronously);

            void Handler(object? sender, byte[] frame)
            {
                if (!CompanyFrame.TryParse(frame, out var baseCmd, out var type, out var data) ||
                    baseCmd != CompanyFrame.BaseCmdResponse || type != CompanyCmd.StreamTelemetry)
                    return;
                if (!_codec.TryParseStreamResp(data, expectSubCmd, out var status, out var div))
                    return;
                tcs.TrySetResult((status, div));
            }

            _serialChannel.FrameReceived += Handler;
            try
            {
                await _serialChannel.SendAsync(request);
                await Task.WhenAny(tcs.Task, Task.Delay(_config.ResponseTimeoutMs));
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

    // ============ 绘图 ============

    private const double W = 320, H = 150, ML = 34, MR = 8, MT = 8, MB = 20;

    /// <summary>窗口 = 最近 WindowMs 毫秒（回放=全量；实时=滚动窗）</summary>
    private const int WindowMs = 10_000;

    private void RefreshCharts()
    {
        List<(long Ts, TelemetryFrame F)> snap;
        lock (_gate)
        {
            if (_samples.Count == 0)
            {
                StatsText = IsStreaming ? StatsText : "";
                UpdateLiveReadout(null);
                return;
            }
            var first = _samples[^1].Ts - WindowMs;
            snap = _replayMode ? _samples.ToList() : _samples.SkipWhile(s => s.Ts < first).ToList();
        }
        UpdateLiveReadout(snap[^1].F);

        double dpsPerLsb = 0.01;   // 定点定案（对齐卷 06 §1）：陀螺 raw=0.01dps 直读物理量，无 FS 换算
        long t0 = snap[0].Ts, t1 = snap[^1].Ts;
        double span = Math.Max(1, t1 - t0);
        double xOf(long ts) => ML + (ts - t0) / span * (W - ML - MR);

        // —— 原始时域：raw_gx/gz → dps，自动取整刻度
        double rawMax = Math.Max(1e-3, snap.Max(s => Math.Max(Math.Abs(s.F.RawGx), Math.Abs(s.F.RawGz))) * dpsPerLsb);
        double rawScale = NiceCeiling(rawMax);
        double rawYOf(double dps) => MT + (1 - (dps + rawScale) / (2 * rawScale)) * (H - MT - MB);
        RawGxPoints = Line(snap, s => xOf(s.Ts), s => rawYOf(s.F.RawGx * dpsPerLsb));
        RawGzPoints = Line(snap, s => xOf(s.Ts), s => rawYOf(s.F.RawGz * dpsPerLsb));
        RawScaleText = $"±{rawScale:0.#}dps";

        // —— 输出时域：out_x/out_y（i8 原值；窗内自动刻度，缺省 ±16）
        int outMax = Math.Max(8, snap.Max(s => Math.Max(Math.Abs((int)s.F.OutX), Math.Abs((int)s.F.OutY))));
        double outScale = NiceCeiling(outMax);
        double outYOf(double v) => MT + (1 - (v + outScale) / (2 * outScale)) * (H - MT - MB);
        OutXPoints = Line(snap, s => xOf(s.Ts), s => outYOf(s.F.OutX));
        OutYPoints = Line(snap, s => xOf(s.Ts), s => outYOf(s.F.OutY));
        RateOverlayPoints = ShowRateOverlay
            ? Line(snap, s => xOf(s.Ts), s => outYOf(s.F.RateYaw * 0.01 / 10))   // rate 叠加：0.01dps→÷10 折到 out 量级（仅形状参考）
            : new PointCollection();
        DiagOverlayPoints = OverlayDiag
            ? Line(snap, s => xOf(s.Ts), s =>
            {
                double norm = s.F.RawGx * dpsPerLsb / rawScale;   // 原始归一化到 ±1
                return MT + (1 - (norm + outScale) / (2 * outScale)) * (H - MT - MB);
            })
            : new PointCollection();

        // —— 输入-输出曲线：|rate_yaw| 分桶（5dps 步，0~200）vs |out_x| 均值
        var buckets = new double[40];
        var counts = new int[40];
        foreach (var s in snap)
        {
            double dps = Math.Abs(s.F.RateYaw) * 0.01;
            int b = Math.Min(39, (int)(dps / 5));
            buckets[b] += Math.Abs(s.F.OutX);
            counts[b]++;
        }
        var io = new PointCollection();
        double ioMax = Math.Max(1, outScale);
        bool any = false;
        for (int b = 0; b < 40; b++)
        {
            if (counts[b] == 0)
                continue;
            any = true;
            double x = ML + (b * 5 + 2.5) / 200.0 * (W - ML - MR);
            double y = MT + (1 - buckets[b] / counts[b] / ioMax) * (H - MT - MB);
            io.Add(new Point(x, y));
        }
        IoPoints = any ? io : new PointCollection();
        IoScaleText = $"|rate| 0~200dps → |out_x| 均值（y 上限 ±{ioMax:0.#}）";

        double fps = FpsOf(snap);
        StatsText = $"{snap.Count} 帧 @ {fps:F1}fps";
    }

    private static PointCollection Line(IEnumerable<(long Ts, TelemetryFrame F)> snap,
        Func<(long Ts, TelemetryFrame F), double> x, Func<(long Ts, TelemetryFrame F), double> y)
    {
        var pts = new PointCollection();
        foreach (var s in snap)
            pts.Add(new Point(x(s), y(s)));
        return pts;
    }

    private static double NiceCeiling(double v)
    {
        double[] nice = { 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000 };
        foreach (var n in nice)
            if (v <= n)
                return n;
        return Math.Ceiling(v / 5000) * 5000;
    }

    private static double FpsOf(IReadOnlyList<(long Ts, TelemetryFrame F)> snap) =>
        snap.Count < 2 ? 0 : (snap.Count - 1) * 1000.0 / Math.Max(1, snap[^1].Ts - snap[0].Ts);

    private static int CountDropped(IReadOnlyList<(long Ts, TelemetryFrame F)> snap)
    {
        int dropped = 0;
        for (int i = 1; i < snap.Count; i++)
        {
            int delta = (snap[i].F.Seq - snap[i - 1].F.Seq) & 0xFFFF;
            if (delta > 1 && delta <= 0x8000)
                dropped += delta - 1;
        }
        return dropped;
    }
}

/// <summary>CSV 录波文件读取（与 <see cref="TelemetryCsv"/> 同格式；仅本页回放用）</summary>
internal static class CsvFile
{
    private const string Header = "pc_rx_ts,seq,flags,raw_gx,raw_gy,raw_gz,raw_ax,raw_ay,raw_az,rate_yaw,rate_roll,out_x,out_y";

    public static List<(long Ts, TelemetryFrame F)> ReadAll(string path)
    {
        var rows = new List<(long, TelemetryFrame)>();
        foreach (var line in File.ReadLines(path))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith("#") || t.StartsWith("pc_rx_ts"))
                continue;
            var parts = t.Split(',');
            // 13 列=v1 旧录波（无 temp），14 列=v2（尾列 temp_imu）
            if (parts.Length is not (13 or 14))
                continue;
            try
            {
                rows.Add((long.Parse(parts[0]), new TelemetryFrame(
                    ushort.Parse(parts[1]), byte.Parse(parts[2]),
                    short.Parse(parts[3]), short.Parse(parts[4]), short.Parse(parts[5]),
                    short.Parse(parts[6]), short.Parse(parts[7]), short.Parse(parts[8]),
                    short.Parse(parts[9]), short.Parse(parts[10]),
                    sbyte.Parse(parts[11]), sbyte.Parse(parts[12]),
                    parts.Length >= 14 ? short.Parse(parts[13]) : (short)0)));
            }
            catch (FormatException)
            {
                // 脏行跳过（半截写入的尾行）
            }
        }
        return rows;
    }
}
