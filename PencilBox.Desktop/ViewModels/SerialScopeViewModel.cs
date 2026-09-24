using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PencilBox.Core.Communication;
using PencilBox.Core.SerialScope;
using PencilBox.Desktop.Helpers;
using PencilBox.Infrastructure.Serial;

namespace PencilBox.Desktop.ViewModels;

/// <summary>可选的串口（带**类型标注**，避免选到调试器虚拟口 / 蓝牙口，05 §3.5）</summary>
public sealed class SerialPortChoice
{
    public required string PortName { get; init; }
    public required SerialPortRole Role { get; init; }

    /// <summary>下拉里显示的一行：`COM7 · USB转串口`</summary>
    public string Display => $"{PortName} · {SerialPortClassifier.Text(Role)}";

    /// <summary>选中时的前置提示（正常口不打扰）</summary>
    public string? Warning => SerialPortClassifier.SelectionWarning(Role);
}

/// <summary>打印区的一行（已拍平成字符串，便于 ListBox 虚拟化直出）</summary>
public sealed class ScopeLineRow
{
    public required string Time { get; init; }
    public required string Port { get; init; }
    public required string Domain { get; init; }
    public required string Text { get; init; }

    /// <summary>是否 F5 帧（整行**高亮**，05 §3.5）</summary>
    public required bool IsFrame { get; init; }

    /// <summary>域着色（05 §3.3「UI 可分别过滤/着色」）</summary>
    public required Brush Foreground { get; init; }

    /// <summary>帧底色（淡黄，一眼与设备文本分开）</summary>
    public required Brush Background { get; init; }

    public static ScopeLineRow From(LogLine line) => new()
    {
        Time = line.Timestamp.ToString("HH:mm:ss.fff"),
        Port = line.PortName,
        Domain = line.Domain,
        Text = line.Decoded,
        IsFrame = line.Source == LogSource.Frame,
        Foreground = BrushFor(line.Domain),
        Background = line.Source == LogSource.Frame ? FrameBackground : Brushes.Transparent
    };

    private static readonly Brush FrameBackground = Freeze(Color.FromRgb(0xFF, 0xF6, 0xDC));

    private static readonly Dictionary<string, Brush> DomainBrushes = new(StringComparer.Ordinal)
    {
        [LogDomain.OtaUsb] = Freeze(Color.FromRgb(0x1E, 0x6F, 0xB4)),      // 蓝：升级内部
        [LogDomain.F5] = Freeze(Color.FromRgb(0xB0, 0x30, 0x60)),          // 洋红：公司协议
        [LogDomain.Pair] = Freeze(Color.FromRgb(0x7A, 0x3E, 0xA8)),        // 紫：配对
        [LogDomain.GattClient] = Freeze(Color.FromRgb(0x2E, 0x8B, 0x57)),  // 绿：BLE 扫描
        [LogDomain.Ble] = Freeze(Color.FromRgb(0x2E, 0x8B, 0x57)),
        [LogDomain.Usb] = Freeze(Color.FromRgb(0x4A, 0x6B, 0x8A)),
        [LogDomain.Todo] = Freeze(Color.FromRgb(0xC0, 0x60, 0x00)),        // 橙：占位未接
        [LogDomain.Sys] = Freeze(Color.FromRgb(0x88, 0x88, 0x88)),
        [LogDomain.Other] = Freeze(Color.FromRgb(0x33, 0x33, 0x33))
    };

    private static Brush BrushFor(string domain)
        => DomainBrushes.TryGetValue(domain, out var brush) ? brush : DomainBrushes[LogDomain.Other];

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// 串口页（05 §3.5 全部要求）—— **设备日志 + F5 协议**这条线，不是通用串口助手（05 §5.5）。
///
/// 落点说明：
/// - 双口可选/可单独/可同时，各自带类型标注与波特率（含 1M/2M）；
/// - 打印区**虚拟化列表 + 统一时间轴**（修掉旧页"只有一行"），可暂停（勾掉自动落底）/按端口按域过滤/高亮 F5 帧；
/// - 三份日志同时保留，路径可见、可打开目录；启动前可选清空（默认清空并显著提示）；
/// - 一键对拍摘要（与 CLI `serial diff` 共用 <see cref="SerialScopeDiffService"/>）。
///
/// 线程模型：落盘泵线程 → <see cref="ConcurrentQueue{T}"/> → <see cref="DispatcherTimer"/> 每 200ms
/// 批量刷进 UI。不这么做的话，几十行/秒的端口会让每行一次 Dispatcher 往返，界面直接卡住。
/// </summary>
public partial class SerialScopeViewModel : ObservableObject
{
    /// <summary>打印区上限（超出后成批裁掉最老的，避免长时间运行内存无限涨）</summary>
    private const int MaxRows = 3000;

    /// <summary>一次裁掉的行数（成批裁比逐行裁便宜得多）</summary>
    private const int TrimBatch = 400;

    private readonly ISerialTransport _serialTransport;
    private readonly SerialLogSession _logSession;
    private readonly SerialScopeDiffService _diffService;

    private readonly ConcurrentQueue<LogLine> _incoming = new();
    private readonly List<ScopeLineRow> _allRows = new();
    private readonly DispatcherTimer _uiPump;

    private CancellationTokenSource? _cts;

    public SerialScopeViewModel(
        ISerialTransport serialTransport,
        SerialLogSession logSession,
        SerialScopeDiffService diffService)
    {
        _serialTransport = serialTransport;
        _logSession = logSession;
        _diffService = diffService;

        _uiPump = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _uiPump.Tick += (_, _) => DrainIncoming();
        _uiPump.Start();

        LogDir = SerialScopePaths.ResolveLogDir(null);
        _ = RefreshPortsAsync();
        UpdateHints();
    }

    // ===== 端口 =====

    public ObservableCollection<SerialPortChoice> Ports { get; } = new();

    [ObservableProperty]
    private SerialPortChoice? _port1;

    [ObservableProperty]
    private bool _usePort2;

    [ObservableProperty]
    private SerialPortChoice? _port2;

    /// <summary>端口 1 标签（写进每行 `[NAME]`）</summary>
    [ObservableProperty]
    private string _label1 = "PENCIL";

    [ObservableProperty]
    private string _label2 = "DONGLE";

    /// <summary>波特率候选：**补上我们实际在用的 1M / 2M**（旧页缺这两个，05 §1）</summary>
    public List<int> BaudRates { get; } = new()
    {
        9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600, 1_000_000, 2_000_000
    };

    [ObservableProperty]
    private int _baud1 = 1_000_000;

    [ObservableProperty]
    private int _baud2 = 115_200;

    // ===== 启动前开关 =====

    /// <summary>启动前是否清空原日志（**默认清空**，05 §3.5；取消勾选 = 追加）</summary>
    [ObservableProperty]
    private bool _clearOnStart = true;

    /// <summary>是否把 F5 帧解码成人类可读（关 = 原样 HEX，仍定帧、不搅乱文本）</summary>
    [ObservableProperty]
    private bool _decodeFrames = true;

    /// <summary>是否同时写 JSONL（机读）</summary>
    [ObservableProperty]
    private bool _jsonlEnabled = true;

    /// <summary>轮转 `上限:份数`（如 `10M:10`）</summary>
    [ObservableProperty]
    private string _rotateText = "10M:10";

    // ===== 运行态 =====

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartButtonText))]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = "未启动";

    [ObservableProperty]
    private string _logDir = string.Empty;

    /// <summary>自动落底（取消勾选 = 暂停滚动，05 §3.5「可暂停」）</summary>
    [ObservableProperty]
    private bool _autoScroll = true;

    /// <summary>打印区（虚拟化列表的数据源；已按过滤条件筛过）</summary>
    public BulkObservableCollection<ScopeLineRow> Lines { get; } = new();

    // ===== 过滤 =====

    /// <summary>显示端口 1 的行</summary>
    [ObservableProperty]
    private bool _showPort1 = true;

    /// <summary>显示端口 2 的行</summary>
    [ObservableProperty]
    private bool _showPort2 = true;

    /// <summary>只看 F5 帧</summary>
    [ObservableProperty]
    private bool _onlyFrames;

    /// <summary>域过滤（`全部` = 不过滤）</summary>
    [ObservableProperty]
    private string _domainFilter = AllDomains;

    public const string AllDomains = "全部";

    public ObservableCollection<string> DomainChoices { get; } = new()
    {
        AllDomains,
        LogDomain.OtaUsb, LogDomain.F5, LogDomain.Pair, LogDomain.GattClient, LogDomain.Todo, LogDomain.Sys, LogDomain.Other
    };

    // ===== 对拍 =====

    [ObservableProperty]
    private string _diffVerdict = "（未对拍）";

    [ObservableProperty]
    private string _diffText = "点「对拍」：以统一时间轴为基准，把 PC 收发帧与设备日志对齐，给出缺失/重查/终态。";

    // ===== 派生显示 =====

    public string StartButtonText => IsRunning ? "抓取中…" : "开始抓日志";

    /// <summary>主日志路径（界面直接显示，05 §3.5「路径可见」）</summary>
    public string MainLogPath => SerialLogFileReader.ResolveMainPath(LogDir);

    /// <summary>另外两份日志的说明</summary>
    public string PerPortLogHint =>
        $"每端口：{(string.IsNullOrWhiteSpace(Label1) ? "端口1" : Label1.Trim())}.log" +
        (UsePort2 ? $" / {(string.IsNullOrWhiteSpace(Label2) ? "端口2" : Label2.Trim())}.log" : string.Empty) +
        $" · 机读：{SerialLogWriter.JsonlFileName}";

    /// <summary>端口纪律提示（独占 + 选中端口的类型提醒，05 §3.5）</summary>
    [ObservableProperty]
    private string _portHints = string.Empty;

    // ===== 命令 =====

    [RelayCommand]
    private async Task RefreshPortsAsync()
    {
        try
        {
            var ports = await _serialTransport.EnumeratePortsAsync();

            MainThreadHelper.EnsureOnMainThread(() =>
            {
                var previous1 = Port1?.PortName;
                var previous2 = Port2?.PortName;

                Ports.Clear();

                // 默认顺序按"更像我们的设备"排（USB 转串口桥 > 调试器虚拟口 > 未知 > 蓝牙），
                // 免得默认选中一个蓝牙口（05 §3.5「避免选到 JTAG 虚拟口」的精神）
                foreach (var port in ports
                             .OrderBy(p => SerialPortClassifier.Preference(SerialPortClassifier.Classify(p.Description)))
                             .ThenBy(p => p.PortName, StringComparer.OrdinalIgnoreCase))
                {
                    Ports.Add(new SerialPortChoice
                    {
                        PortName = port.PortName,
                        Role = SerialPortClassifier.Classify(port.Description)
                    });
                }

                Port1 = Ports.FirstOrDefault(p => p.PortName == previous1) ?? Ports.FirstOrDefault();
                Port2 = Ports.FirstOrDefault(p => p.PortName == previous2) ?? Ports.Skip(1).FirstOrDefault();
                StatusText = $"找到 {ports.Count} 个串口";
                UpdateHints();
            });
        }
        catch (Exception ex)
        {
            StatusText = $"枚举串口失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void Start()
    {
        if (IsRunning)
            return;

        if (Port1 is null)
        {
            StatusText = "请先选择端口 1";
            return;
        }

        if (UsePort2 && Port2 is null)
        {
            StatusText = "端口 2 已启用但未选择";
            return;
        }

        if (!LogRotationPolicy.TryParse(RotateText, out var rotation))
        {
            StatusText = $"轮转格式非法：{RotateText}（应为 10M:10）";
            return;
        }

        var ports = new List<SerialScopePortSpec>
        {
            new() { Name = LabelOf(Port1, Label1), Port = Port1.PortName, BaudRate = Baud1 }
        };

        if (UsePort2 && Port2 is not null)
            ports.Add(new SerialScopePortSpec { Name = LabelOf(Port2, Label2), Port = Port2.PortName, BaudRate = Baud2 });

        var request = new SerialLogSessionRequest
        {
            Ports = ports,
            LogDir = LogDir,
            Rotation = rotation,
            Jsonl = JsonlEnabled,
            DecodeFrames = DecodeFrames,
            ClearOnStart = ClearOnStart,
            Report = message => MainThreadHelper.EnsureOnMainThread(() => StatusText = message),
            OnLine = line => _incoming.Enqueue(line)
        };

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsRunning = true;
        StatusText = "启动中…";

        // 不 await：Start 要立刻返回，界面才能响应「停止」
        _ = Task.Run(async () =>
        {
            try
            {
                int code = await _logSession.RunAsync(request, token);
                MainThreadHelper.EnsureOnMainThread(() => StatusText = code == 0 ? "已停止" : "启动失败（见上方状态行）");
            }
            catch (Exception ex)
            {
                MainThreadHelper.EnsureOnMainThread(() => StatusText = $"异常退出：{ex.Message}");
            }
            finally
            {
                MainThreadHelper.EnsureOnMainThread(() =>
                {
                    IsRunning = false;
                    OnPropertyChanged(nameof(MainLogPath));
                });
            }
        }, CancellationToken.None);
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    /// <summary>清屏（**只清显示**，日志文件继续落盘 —— 避免误以为把证据删了）</summary>
    [RelayCommand]
    private void ClearScreen()
    {
        _allRows.Clear();
        Lines.Clear();
        StatusText = "打印区已清屏（日志文件不动，仍在继续落盘）";
    }

    /// <summary>打开日志目录（05 §3.5「可打开目录」）</summary>
    [RelayCommand]
    private void OpenLogDir()
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LogDir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = $"打开目录失败：{ex.Message}";
        }
    }

    /// <summary>对拍（与 CLI `serial diff` 同一套逻辑；抓取中也能对拍 —— 读取用共享模式）</summary>
    [RelayCommand]
    private void Compare()
    {
        try
        {
            var diff = _diffService.Compare(LogDir, allSessions: false, portFilter: null);
            DiffVerdict = diff.Verdict;
            DiffText = diff.ToText();
        }
        catch (FileNotFoundException)
        {
            DiffVerdict = "（还没有日志）";
            DiffText = $"日志文件不存在：{MainLogPath}{Environment.NewLine}先点「开始抓日志」抓一段再来对拍。";
        }
        catch (Exception ex)
        {
            DiffVerdict = "（对拍失败）";
            DiffText = ex.Message;
        }
    }

    // ===== 属性联动 =====

    partial void OnPort1Changed(SerialPortChoice? value) => UpdateHints();

    partial void OnPort2Changed(SerialPortChoice? value) => UpdateHints();

    partial void OnUsePort2Changed(bool value)
    {
        UpdateHints();
        OnPropertyChanged(nameof(PerPortLogHint));
    }

    partial void OnLabel1Changed(string value) => OnPropertyChanged(nameof(PerPortLogHint));

    partial void OnLabel2Changed(string value) => OnPropertyChanged(nameof(PerPortLogHint));

    // 过滤条件变化 ⇒ 用存量行重建可见列表（按行增量是快路径，改条件才重建）
    partial void OnShowPort1Changed(bool value) => RebuildVisible();

    partial void OnShowPort2Changed(bool value) => RebuildVisible();

    partial void OnOnlyFramesChanged(bool value) => RebuildVisible();

    partial void OnDomainFilterChanged(string value) => RebuildVisible();

    // ===== 刷屏 =====

    /// <summary>UI 泵：把后台攒下的行批量刷进列表（在 UI 线程上跑）</summary>
    private void DrainIncoming()
    {
        while (_incoming.TryDequeue(out var line))
        {
            var row = ScopeLineRow.From(line);
            _allRows.Add(row);

            if (Passes(row))
                Lines.Add(row);
        }

        if (_allRows.Count <= MaxRows)
            return;

        // 成批裁掉最老的：可见列表也同步裁，避免"看不见的行还占着"
        int cut = _allRows.Count - MaxRows + TrimBatch;
        _allRows.RemoveRange(0, cut);
        Lines.TrimHead(cut);
    }

    private void RebuildVisible()
        => Lines.Reset(_allRows.Where(Passes));

    private bool Passes(ScopeLineRow row)
    {
        if (!ShowPort1 && row.Port == LabelOf(Port1, Label1))
            return false;
        if (!ShowPort2 && row.Port == LabelOf(Port2, Label2))
            return false;
        if (OnlyFrames && !row.IsFrame)
            return false;
        if (DomainFilter != AllDomains && !string.Equals(row.Domain, DomainFilter, StringComparison.Ordinal))
            return false;
        return true;
    }

    /// <summary>该端口的标签（空标签则退回端口名 —— 与抓日志时的口径一致）</summary>
    private static string LabelOf(SerialPortChoice? port, string label)
        => string.IsNullOrWhiteSpace(label) ? port?.PortName ?? string.Empty : label.Trim();

    /// <summary>端口纪律提示：独占 + 选中端口的类型提醒</summary>
    private void UpdateHints()
    {
        var hints = new List<string>
        {
            "COM 口独占：开抓前请确认没有其它程序（含 company 命令 / 另一路抓日志）在占用；用完即停。"
        };

        if (Port1?.Warning is { } warning1)
            hints.Add($"端口 1 {Port1.PortName}：{warning1}");
        if (UsePort2 && Port2?.Warning is { } warning2)
            hints.Add($"端口 2 {Port2.PortName}：{warning2}");

        PortHints = string.Join(Environment.NewLine, hints);
    }
}
