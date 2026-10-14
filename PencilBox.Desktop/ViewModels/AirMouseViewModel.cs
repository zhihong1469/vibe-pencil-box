// <copyright file="AirMouseViewModel.cs" company="PencilBox Contributors">
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
using PencilBox.Core.Communication;
using PencilBox.Core.Configuration;
using PencilBox.Core.Fsm;
using PencilBox.Core.Protocol;
using PencilBox.Core.Protocol.Company;
using PencilBox.Core.Services;
using PencilBox.Infrastructure.Serial;
using Microsoft.Win32;

namespace PencilBox.Desktop.ViewModels;

/// <summary>
/// `空鼠` 调参工作台（issues-pencil/03：基础页升级版）。
///
/// 布局=连接栏 + 可写参数卡片（0x01~0x08：滑条/步进/折页文案/★偏离/防抖自动下发；0x09~0x0B 只读/控制型走 CLI）
/// + 增益曲线实时图（0x02/03/04 联动）+ 0x01×0x02 嵌套助手 + 0x62 遥测流三视图（Telemetry，页内下方）
/// + 会话面板（方案快照 A/B、恢复出厂、变更历史、导出宏片段、试手感清单）。
/// 通信与 CLI 完全同源（同一 0x61/01 读、0x61/02 写帧；<see cref="ITuningService"/>）；
/// 快照经 <see cref="IAirMouseSnapshotStore"/> 落 `configs/airmouse/`，与 CLI `param snapshot` 互通。
/// </summary>
public partial class AirMouseViewModel : ObservableObject
{
    private readonly ITuningService _tuning;
    private readonly ISerialTransport _serialTransport;
    private readonly ICompanyCodec _codec;
    private readonly IAirMouseSnapshotStore _snapshots;
    private readonly SerialCommandChannel _serialChannel;
    private readonly CommandRequester _requester;
    private readonly CompanyConfig _config;
    private readonly DispatcherTimer _debounceTimer;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly HashSet<AirMouseCardViewModel> _pending = new();
    private bool _draining;

    /// <summary>0x01×0x02 嵌套基准（小范围有效增益 0.08；03 卷 §5，设置化留后续）</summary>
    private const float BaselineEffectiveGain = 0.08f;

    /// <summary>遥测流与录波子区（0x62，对齐卷 06；内嵌「空鼠」页下方，与调参共用同一串口连接）</summary>
    public TelemetryViewModel Telemetry { get; }

    public AirMouseViewModel(ITuningService tuning, ISerialTransport serialTransport,
        SerialCommandChannel serialChannel, ICompanyCodec codec, IAirMouseSnapshotStore snapshots,
        IClock clock, CompanyConfig config)
    {
        _tuning = tuning;
        _serialTransport = serialTransport;
        _serialChannel = serialChannel;
        _codec = codec;
        _snapshots = snapshots;
        _requester = new CommandRequester(clock, config);
        _config = config;
        Telemetry = new TelemetryViewModel(serialTransport, serialChannel, codec, clock, config);

        // 只为可写参数（0x01~0x08）建卡：0x09/0x0A 只读、0x0B 固化控制走 CLI（01 卷 §5.1）
        foreach (var d in TuningParamTable.Tunable)
        {
            var card = new AirMouseCardViewModel(d);
            card.CommitRequested += MarkPending;
            Cards.Add(card);
        }

        _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounceTimer.Tick += async (_, _) => await DrainPendingAsync();

        _serialTransport.ConnectionStateChanged += OnTransportConnectionChanged;
        RefreshSnapshotNames();
    }

    // ============ 连接栏（03 §2） ============

    public ObservableCollection<string> SerialPorts { get; } = new();

    [ObservableProperty]
    private string? _selectedPort;

    /// <summary>绿=已连接 / 灰=未连接 / 红=连接丢失（写参数超时或传输层断开——03 §2 状态灯）</summary>
    [ObservableProperty]
    private AirMouseLinkState _linkState = AirMouseLinkState.Disconnected;

    partial void OnLinkStateChanged(AirMouseLinkState value)
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsNotConnected));
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        LoadAllCommand.NotifyCanExecuteChanged();
        ApplySuggestionCommand.NotifyCanExecuteChanged();
        SaveSnapshotCommand.NotifyCanExecuteChanged();
        ApplySnapshotCommand.NotifyCanExecuteChanged();
        RestoreFactoryCommand.NotifyCanExecuteChanged();
        ExportMacroCommand.NotifyCanExecuteChanged();
    }

    public bool IsConnected => LinkState == AirMouseLinkState.Connected;
    public bool IsNotConnected => !IsConnected;

    [ObservableProperty]
    private string _firmwareText = "未连接";

    [ObservableProperty]
    private string _statusMessage = "选笔端串口 → 连接（0x03 读版本）→ 自动读全表。参数仅 RAM：改完试手感，满意后导出宏片段交固件回填";

    [RelayCommand]
    private async Task RefreshPortsAsync()
    {
        try
        {
            var ports = await _serialTransport.EnumeratePortsAsync();
            var names = ports.Select(p => p.PortName).ToList();
            SerialPorts.Clear();
            foreach (var n in names)
                SerialPorts.Add(n);
            if (SelectedPort == null && names.Count > 0)
                SelectedPort = names[0];
        }
        catch (Exception ex)
        {
            StatusMessage = $"列串口失败：{ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(IsNotConnected))]
    private async Task ConnectAsync()
    {
        if (SelectedPort == null)
        {
            StatusMessage = "先选笔端串口（0x61 仅串口轨，基站零感知）";
            return;
        }

        try
        {
            var settings = new SerialPortSettings { BaudRate = _config.SerialBaudRate };
            if (!await _serialTransport.ConnectAsync(SelectedPort, settings))
            {
                StatusMessage = $"打开串口失败：{SelectedPort}——串口被占用（请关闭 sscom / 其他 PencilBox 串口会话）或端口号有误";
                return;
            }

            LinkState = AirMouseLinkState.Connected;
            FirmwareText = "读版本中…";
            StatusMessage = $"已连接 {SelectedPort}，读取版本与参数表…";
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(IsNotConnected));
            ConnectCommand.NotifyCanExecuteChanged();
            DisconnectCommand.NotifyCanExecuteChanged();

            await ReadVersionIntoBarAsync();
            await LoadAllAsync(source: "连接自动刷新");
        }
        catch (Exception ex)
        {
            LinkState = AirMouseLinkState.Disconnected;
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(IsNotConnected));
            StatusMessage = $"连接失败：{ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task DisconnectAsync()
    {
        try
        {
            await _serialTransport.DisconnectAsync();
        }
        catch
        {
            // 断开失败也按未连接处理（重连路径会重置）
        }

        LinkState = AirMouseLinkState.Disconnected;
        FirmwareText = "未连接";
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsNotConnected));
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        StatusMessage = "已断开（笔端参数保持 RAM 现值；重连后自动全表读刷新）";
    }

    private void OnTransportConnectionChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        if (e.IsConnected)
            return;
        Application.Current?.Dispatcher.Invoke(() =>
        {
            LinkState = AirMouseLinkState.Lost;
            FirmwareText = "连接丢失";
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(IsNotConnected));
            ConnectCommand.NotifyCanExecuteChanged();
            DisconnectCommand.NotifyCanExecuteChanged();
            StatusMessage = "串口连接丢失（拔线/对端复位）——重新连接后自动全表读刷新（笔端掉电失忆是特性）";
        });
    }

    /// <summary>0x03 读型号/版本（串口轨 target=0x00 笔端自答）；读不到不阻塞（标"版本未知"）</summary>
    private async Task ReadVersionIntoBarAsync()
    {
        var resp = await _requester.RequestAsync(_serialChannel, _codec.EncodeReadVersion(), CompanyCmd.ReadVersion);
        if (resp != null && _codec.TryParseVersionResp(resp, out var ver) && ver.Status == CompanyStatus.Ok)
            FirmwareText = $"MODEL=0x{ver.Model:X2}   固件 {ver.SverMajor}.{ver.SverMinor}.{ver.SverPatch}";
        else
            FirmwareText = "版本未知（0x03 无应答——不影响调参）";
    }

    // ============ 参数卡片（03 §3） ============

    public ObservableCollection<AirMouseCardViewModel> Cards { get; } = new();

    /// <summary>读取全表（连接后自动；手动按钮同入口）</summary>
    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task LoadAllAsync(object? parameter) => await LoadAllAsync(source: "手动刷新");

    private async Task LoadAllAsync(string source)
    {
        try
        {
            var items = await _tuning.ReadAllAsync(SelectedPort!);
            var byId = items.ToDictionary(r => r.Id, r => r.Value);

            foreach (var card in Cards)
            {
                if (byId.TryGetValue(card.D.Id, out var v))
                    card.SetEcho(v);
            }

            var unknown = items.Where(r => TuningParamTable.Find(r.Id) == null).ToList();
            StatusMessage = $"全表已读 {items.Count} 项（{source}）" +
                            (unknown.Count > 0
                                ? $"；未知 ID：{string.Join("、", unknown.Select(u => $"0x{u.Id:X2}"))}（工具表未收录，原样显示）"
                                : "");
            RefreshCurveAndLinkage();
        }
        catch (TuningNoAnswerException)
        {
            // 无应答 ≠ 链路断：笔端未实现 0x61 阶段 100% 发生且属预期（串口本体未动，
            // 链路判定只认传输层 ConnectionStateChanged——勿再按消息文本打 Lost）
            StatusMessage = "读取无应答——笔端 0x61 未实现前属预期（链路未断，无需重连）；固件上线后此处即出数";
        }
        catch (Exception ex)
        {
            StatusMessage = $"读取失败：{ex.Message}";
        }
        finally
        {
            OnPropertyChanged(nameof(IsConnected));
        }
    }

    /// <summary>卡片值有改动 → 重挂 300ms 防抖（03 §3：拖动结束/数值确认后自动下发）</summary>
    private void MarkPending(AirMouseCardViewModel card)
    {
        card.ErrorText = null;
        _pending.Add(card);
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private async Task DrainPendingAsync()
    {
        _debounceTimer.Stop();
        if (_draining)
            return;          // 在写就不再重入；卡片改动会再次触发防抖
        _draining = true;
        try
        {
            while (_pending.Count > 0)
            {
                var card = _pending.First();
                _pending.Remove(card);
                await WriteCardAsync(card, source: "手动");
            }
        }
        finally
        {
            _draining = false;
        }
    }

    /// <summary>写一参（03 §7-3 错误面：02 红条+回弹旧值；03 OTA 提示；无应答→红灯）</summary>
    private async Task<bool> WriteCardAsync(AirMouseCardViewModel card, string source)
    {
        if (!card.TryGetDesired(out var desired))
        {
            card.ErrorText = $"数值 \"{card.TextValue}\" 不是合法数字";
            return false;
        }

        if (OtaSessionActivity.InProgress)
        {
            card.ErrorText = "OTA 升级会话进行中，写被拦（笔端亦会以 STATUS=03 拒收）；请先退出升级";
            return false;
        }

        await _writeGate.WaitAsync();
        try
        {
            card.IsWriting = true;
            var oldEcho = card.EchoValue;

            var result = await _tuning.WriteAsync(SelectedPort!, card.D.Id, desired);
            switch (result.Status)
            {
                case TuningStatus.Ok:
                    card.SetEcho(result.Effective);
                    AddHistory($"0x{card.D.Id:X2} {card.D.Name}  {TuningParamDescriptor.Format(oldEcho)} → {TuningParamDescriptor.Format(result.Effective)}", source);
                    if (card.D.Id is 0x02 or 0x03 or 0x04)
                        SnapshotPrevCurve();
                    RefreshCurveAndLinkage();
                    card.ErrorText = null;
                    return true;

                case TuningStatus.OutOfRange:
                    card.SetEcho(result.Effective);   // 笔端回写前旧值 → 回弹
                    card.ErrorText = $"越界拒绝：允许范围 {card.D.RangeText}（原值 {TuningParamDescriptor.Format(result.Effective)} 未动，笔端不夹取）";
                    return false;

                case TuningStatus.OtaBusy:
                    card.ErrorText = "OTA 升级会话进行中，写被拒：请先退出升级再调参";
                    return false;

                case TuningStatus.UnknownId:
                    card.ErrorText = $"未知参数 ID=0x{card.D.Id:X2}（笔端不识别）——建议重新读取全表";
                    _ = LoadAllAsync(source: "未知 ID 后自动刷新");
                    return false;

                default:
                    card.ErrorText = $"写失败：STATUS=0x{result.Status:X2}";
                    return false;
            }
        }
        catch (TuningNoAnswerException)
        {
            // 无应答 ≠ 链路断（笔端未实现 0x61 前属预期；链路判定只认传输层事件）
            card.ErrorText = "无应答——笔端 0x61 未实现前属预期（链路未断，无需重连）";
            StatusMessage = "写参数无应答（500ms×4）——笔端 0x61 未实现前属预期，非断连";
            return false;
        }
        catch (Exception ex)
        {
            card.ErrorText = ex.Message;
            return false;
        }
        finally
        {
            card.IsWriting = false;
            _writeGate.Release();
        }
    }

    // ============ 增益曲线（03 §4）与联动助手（03 §5） ============

    private const double CurveWidth = 320, CurveHeight = 150;
    private const double MarginL = 34, MarginR = 8, MarginT = 8, MarginB = 20;
    private (float Slow, float Knee, float Width)? _prevTrio;

    [ObservableProperty]
    private PointCollection _curvePoints = new();

    [ObservableProperty]
    private PointCollection _prevCurvePoints = new();

    [ObservableProperty]
    private double _kneeTickX;

    [ObservableProperty]
    private double _widthTickX;

    [ObservableProperty]
    private string _kneeTickText = "";

    [ObservableProperty]
    private string _widthTickText = "";

    [ObservableProperty]
    private string _effectiveGainText = "小范围有效增益 = 灵敏度 × 慢速段增益 = -";

    [ObservableProperty]
    private string _stabilityScaleText = "-";

    [ObservableProperty]
    private double _stabilityValue;   // 0=稳 1=飘

    [ObservableProperty]
    private bool _suggestionVisible;

    [ObservableProperty]
    private string _suggestionText = "";

    private AirMouseCardViewModel? Card(byte id) => Cards.FirstOrDefault(c => c.D.Id == id);

    private static double Shape(double v, double slow, double knee, double width) =>
        v <= knee ? slow :
        v >= knee + width ? 1.0 :
        slow + (1 - slow) * Smooth((v - knee) / width);

    private static double Smooth(double t) => t * t * (3 - 2 * t);   // smoothstep

    /// <summary>实时增益曲线（y=灵敏度×曲线值）+ 拐点/过渡带刻度 + 上一组对比虚线（03 §4）</summary>
    private void RefreshCurveAndLinkage()
    {
        var sens = Card(0x01)?.EchoValue ?? TuningParamTable.All[0].Factory;
        var slow = Card(0x02)?.EchoValue ?? TuningParamTable.All[1].Factory;
        var knee = Card(0x03)?.EchoValue ?? TuningParamTable.All[2].Factory;
        var width = Card(0x04)?.EchoValue ?? TuningParamTable.All[3].Factory;

        double plotW = CurveWidth - MarginL - MarginR, plotH = CurveHeight - MarginT - MarginB;
        double xOf(double v) => MarginL + v / 50.0 * plotW;
        double yMax = Math.Max(sens, 0.05f);
        double yOf(double g) => MarginT + (1 - g / yMax) * plotH;

        var pts = new PointCollection();
        for (int i = 0; i <= 100; i++)
        {
            double v = i * 0.5;
            pts.Add(new Point(xOf(v), yOf(sens * Shape(v, slow, knee, width))));
        }
        CurvePoints = pts;

        KneeTickX = xOf(knee);
        WidthTickX = xOf(knee + width);
        KneeTickText = $"拐点 {TuningParamDescriptor.Format(knee)}";
        WidthTickText = $"+{TuningParamDescriptor.Format(width)}";

        if (_prevTrio is { } p)
        {
            var prev = new PointCollection();
            for (int i = 0; i <= 100; i++)
            {
                double v = i * 0.5;
                prev.Add(new Point(xOf(v), yOf(sens * Shape(v, p.Slow, p.Knee, p.Width))));
            }
            PrevCurvePoints = prev;
        }

        // 联动助手（03 §5）：小范围有效增益 = 灵敏度 × SLOW_GAIN；偏离基准 0.08±10% 弹建议
        float eff = sens * slow;
        EffectiveGainText = $"小范围有效增益 = {TuningParamDescriptor.Format(sens)} × {TuningParamDescriptor.Format(slow)} = {eff:0.000}（基准 0.08：稳←→飘）";
        StabilityValue = Math.Clamp(eff / (BaselineEffectiveGain * 2), 0, 1);
        StabilityScaleText = eff < BaselineEffectiveGain * 0.9 ? "偏稳（微调偏重）" :
                             eff > BaselineEffectiveGain * 1.1 ? "偏飘（手抖易推动）" : "基准区间";

        if (Math.Abs(eff - BaselineEffectiveGain) > BaselineEffectiveGain * 0.1f)
        {
            float suggested = Math.Clamp(BaselineEffectiveGain / Math.Max(sens, 0.01f), 0.05f, 1.00f);
            SuggestionText = $"建议 SLOW_GAIN 调整为 {suggested:0.00#} 以保持小范围手感（0.08 ÷ 灵敏度）。";
            SuggestionVisible = true;
        }
        else
        {
            SuggestionVisible = false;
        }
    }

    /// <summary>记"上一组"对比虚线：任一 0x02/03/04 写成功前，抓当时的 02/03/04 回显组</summary>
    private void SnapshotPrevCurve()
    {
        var slow = Card(0x02)?.EchoValue;
        var knee = Card(0x03)?.EchoValue;
        var width = Card(0x04)?.EchoValue;
        if (slow.HasValue && knee.HasValue && width.HasValue)
            _prevTrio = (slow.Value, knee.Value, width.Value);
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task ApplySuggestionAsync()
    {
        var sens = Card(0x01)?.EchoValue ?? 0.3f;
        var card = Card(0x02);
        if (card == null)
            return;
        float suggested = Math.Clamp(BaselineEffectiveGain / Math.Max(sens, 0.01f), 0.05f, 1.00f);
        card.SetPending(suggested);
        await WriteCardAsync(card, source: "建议应用");
    }

    [RelayCommand]
    private void DismissSuggestion() => SuggestionVisible = false;

    // ============ 会话面板（03 §6） ============

    public ObservableCollection<string> SnapshotNames { get; } = new();

    [ObservableProperty]
    private string? _selectedSnapshot;

    [ObservableProperty]
    private string _snapshotNameInput = "";

    public ObservableCollection<AirMouseHistoryEntry> History { get; } = new();

    [ObservableProperty]
    private string _exportNote = "";

    // 试手感清单（随快照存档，导出时附在报告里）
    [ObservableProperty] private bool _checkClickAccurate;      // 单击准
    [ObservableProperty] private bool _checkDoubleClickInPlace; // 双击原地
    [ObservableProperty] private bool _checkAimStable;          // 瞄准小目标不飘
    [ObservableProperty] private bool _checkFlingReach;         // 快甩到位

    private void RefreshSnapshotNames()
    {
        var names = _snapshots.ListNames();
        SnapshotNames.Clear();
        foreach (var n in names)
            SnapshotNames.Add(n);
        if (SelectedSnapshot == null && SnapshotNames.Count > 0)
            SelectedSnapshot = SnapshotNames[0];
    }

    private void AddHistory(string change, string source) =>
        History.Insert(0, new AirMouseHistoryEntry(DateTime.Now.ToString("HH:mm:ss"), change, source));

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task SaveSnapshotAsync()
    {
        var name = SnapshotNameInput.Trim();
        if (name.Length == 0)
        {
            StatusMessage = "先给方案起个名（建议 A/B 便于互切对比）再保存";
            return;
        }

        try
        {
            var items = await _tuning.ReadAllAsync(SelectedPort!);
            _snapshots.Save(new AirMouseSnapshot(name, DateTime.Now, null, FirmwareText, items, CurrentChecklist()));
            RefreshSnapshotNames();
            SelectedSnapshot = _snapshots.ListNames().Contains(name) ? name : SelectedSnapshot;
            StatusMessage = $"快照 \"{name}\" 已存（configs/airmouse/，与 CLI `param snapshot` 互通）——换个方案再存一个即可 A/B 互切";
        }
        catch (Exception ex)
        {
            StatusMessage = $"快照保存失败：{ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task ApplySnapshotAsync()
    {
        if (SelectedSnapshot == null)
        {
            StatusMessage = "先在下拉里选一个快照";
            return;
        }

        var s = _snapshots.Load(SelectedSnapshot);
        if (s == null)
        {
            StatusMessage = $"快照 \"{SelectedSnapshot}\" 读取失败（可能已被删除）";
            RefreshSnapshotNames();
            return;
        }

        int okCount = 0, failCount = 0;
        foreach (var r in s.Values)
        {
            var card = Card(r.Id);
            if (card == null)
                continue;   // 未知 ID 前向兼容：跳过
            card.SetPending(r.Value);
            if (await WriteCardAsync(card, source: $"快照 {s.Name}"))
                okCount++;
            else
                failCount++;
        }

        if (s.Checklist is { Count: 4 })
        {
            CheckClickAccurate = s.Checklist[0];
            CheckDoubleClickInPlace = s.Checklist[1];
            CheckAimStable = s.Checklist[2];
            CheckFlingReach = s.Checklist[3];
        }

        StatusMessage = failCount == 0
            ? $"快照 \"{s.Name}\" 已应用（{okCount} 项）——直接动笔对比手感"
            : $"快照 \"{s.Name}\" 应用完成但 {failCount} 项失败（见卡片红条）";
    }

    [RelayCommand]
    private void DeleteSnapshot()
    {
        if (SelectedSnapshot == null)
            return;
        _snapshots.Delete(SelectedSnapshot);
        StatusMessage = $"快照 \"{SelectedSnapshot}\" 已删（仅本地记录，笔端不受影响）";
        SelectedSnapshot = null;
        RefreshSnapshotNames();
    }

    /// <summary>恢复出厂（03 §6）：可写参数（0x01~0x08）写回出厂宏值（=CLI `param reset` 同源语义）</summary>
    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task RestoreFactoryAsync()
    {
        int ok = 0;
        foreach (var card in Cards)
        {
            card.SetPending(card.D.Factory);
            if (await WriteCardAsync(card, source: "恢复出厂"))
                ok++;
        }
        StatusMessage = ok == Cards.Count
            ? $"恢复出厂完成（{ok}/{Cards.Count}）——手感回到固件宏基线"
            : $"恢复出厂完成但 {Cards.Count - ok} 项失败（见卡片红条）";
    }

    [RelayCommand]
    private void CopyHistory()
    {
        if (History.Count == 0)
        {
            StatusMessage = "暂无变更历史";
            return;
        }
        var text = string.Join(Environment.NewLine,
            History.Select(h => $"[{h.Time}] {h.Change}（{h.Source}）"));
        Clipboard.SetText(text);
        StatusMessage = $"已复制 {History.Count} 条变更历史到剪贴板";
    }

    /// <summary>导出宏片段（03 §6 核心交付；生成单点=AirMouseMacroSnippet，与 CLI `param export` 同源）</summary>
    [RelayCommand(CanExecute = nameof(IsConnected))]
    private void ExportMacro()
    {
        try
        {
            var readings = Cards.Select(c => new TuningParamReading(c.D.Id, c.EchoValue)).ToList();
            var snippet = AirMouseMacroSnippet.Build(readings, FirmwareText, ExportNote);

            Clipboard.SetText(snippet);

            var dialog = new SaveFileDialog
            {
                Title = "导出空鼠调参宏片段",
                FileName = $"airmouse_macros_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
                Filter = "宏片段 (*.txt;*.c;*.h)|*.txt;*.c;*.h|所有文件 (*.*)|*.*",
            };
            if (dialog.ShowDialog() == true)
                File.WriteAllText(dialog.FileName, snippet, new UTF8Encoding(false));

            StatusMessage = "宏片段已复制到剪贴板" +
                            (dialog.FileName.Length > 0 ? $"，并存到 {dialog.FileName}" : "") +
                            "——交固件侧粘贴回填出包（试手感清单随下次快照存档）";
        }
        catch (Exception ex)
        {
            StatusMessage = $"导出失败：{ex.Message}";
        }
    }

    private IReadOnlyList<bool> CurrentChecklist() =>
        new[] { CheckClickAccurate, CheckDoubleClickInPlace, CheckAimStable, CheckFlingReach };
}

/// <summary>连接状态（03 §2 状态灯）：绿=已连接 / 灰=未连接 / 红=连接丢失</summary>
public enum AirMouseLinkState
{
    Disconnected,
    Connected,
    Lost,
}

/// <summary>变更历史一条（03 §6：时间/参数/旧值→新值/来源）</summary>
public sealed record AirMouseHistoryEntry(string Time, string Change, string Source);

/// <summary>
/// 参数卡片一行 VM：滑条 + 数值框（双向）+ 步进 + 折页文案 + ★偏离 + 红条错误面。
/// 值改动经 <see cref="CommitRequested"/> 交给工作台 VM 做 300ms 防抖与串行写入。
/// </summary>
public partial class AirMouseCardViewModel : ObservableObject
{
    /// <summary>期望值（最近一次 UI 改动；写入时取此值）</summary>
    private float _pendingValue;

    public AirMouseCardViewModel(TuningParamDescriptor d)
    {
        D = d;
        _pendingValue = d.Factory;
        _sliderValue = d.Factory;
        _textValue = TuningParamDescriptor.Format(d.Factory);
        _echoValue = d.Factory;
    }

    public TuningParamDescriptor D { get; }

    public string IdText => $"0x{D.Id:X2}";
    public string HeaderText => $"{IdText}  {D.Name}";
    public double Min => D.Min;
    public double Max => D.Max;
    public double Step => D.Step;
    public string RangeText => D.RangeText;
    public string Unit => D.Unit;
    public string FactoryText => TuningParamDescriptor.Format(D.Factory);
    public string Guide => D.Guide;
    public Visibility AdvancedVisibility => D.Id is 0x06 or 0x07 or 0x08 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>生效值显示文本（<see cref="EchoValue"/> 为 [ObservableProperty] 生成的笔端确认值）</summary>
    public string EchoText => TuningParamDescriptor.Format(EchoValue);

    [ObservableProperty]
    private double _sliderValue;

    [ObservableProperty]
    private string _textValue;

    [ObservableProperty]
    private float _echoValue;

    [ObservableProperty]
    private bool _isModified;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private bool _guideExpanded;

    [ObservableProperty]
    private bool _isWriting;

    public Visibility ModifiedVisibility => IsModified ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ErrorVisibility => ErrorText != null ? Visibility.Visible : Visibility.Collapsed;

    partial void OnErrorTextChanged(string? value) =>
        OnPropertyChanged(nameof(ErrorVisibility));

    /// <summary>UI 改动 → 请求提交（工作台防抖后写）</summary>
    public event Action<AirMouseCardViewModel>? CommitRequested;

    private bool _suppressTextSync;

    partial void OnSliderValueChanged(double value)
    {
        if (!_suppressTextSync)
            TextValue = TuningParamDescriptor.Format((float)value);
        RaiseCommit(value);
    }

    partial void OnTextValueChanged(string value)
    {
        if (float.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            if (Math.Abs(parsed - SliderValue) > 1e-6)
            {
                _suppressTextSync = true;
                SliderValue = Math.Clamp(parsed, D.Min, D.Max);
                _suppressTextSync = false;
            }
            RaiseCommit(parsed);
        }
        // 非法输入不提交：写入时 TryGetDesired 兜底报错（防拖到一半的空串打断防抖）
    }

    partial void OnEchoValueChanged(float value)
    {
        IsModified = Math.Abs(value - D.Factory) > 1e-6f;
        OnPropertyChanged(nameof(EchoText));
    }

    private void RaiseCommit(double desired)
    {
        // **不本地夹取**：越界值原样下发，让笔端以 STATUS=02 拒绝→红条+回弹（03 §7-3 错误面即验收场景 5）
        _pendingValue = (float)desired;
        CommitRequested?.Invoke(this);
    }

    /// <summary>写入时取期望值；文本框非法（空/非数字）时返回 false（红条提示，不发帧）</summary>
    public bool TryGetDesired(out float desired)
    {
        if (float.TryParse(TextValue, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            desired = parsed;
            return true;
        }

        desired = _pendingValue;
        return false;
    }

    /// <summary>程序化设期望值（建议应用/快照/恢复出厂），随后走同一条写入路径</summary>
    public void SetPending(float value)
    {
        var clamped = Math.Clamp(value, D.Min, D.Max);
        _suppressTextSync = true;
        SliderValue = clamped;   // OnSliderValueChanged 会同步文本并提交
        _suppressTextSync = false;
        TextValue = TuningParamDescriptor.Format(clamped);
        _pendingValue = clamped;
        CommitRequested?.Invoke(this);
    }

    /// <summary>笔端确认值落卡（回显/回弹共用）：同步滑条与文本框到生效值</summary>
    public void SetEcho(float value)
    {
        EchoValue = value;
        _suppressTextSync = true;
        SliderValue = Math.Clamp(value, D.Min, D.Max);
        _suppressTextSync = false;
        TextValue = TuningParamDescriptor.Format(value);
        ErrorText = null;
    }

    [RelayCommand]
    private void StepDown() => SetPending((float)(SliderValue - Step));

    [RelayCommand]
    private void StepUp() => SetPending((float)(SliderValue + Step));
}
