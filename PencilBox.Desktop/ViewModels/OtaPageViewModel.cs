using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PencilBox.Core.Communication;
using PencilBox.Core.Fsm;
using PencilBox.Core.Protocol.Company;
using PencilBox.Desktop.Helpers;
using PencilBox.Infrastructure.Hid;
using PencilBox.Infrastructure.Serial;

namespace PencilBox.Desktop.ViewModels;

/// <summary>升级通道（两条，对应公司协议 `0x16` 的会话目标 `DOWNTYPE`）</summary>
public sealed record OtaTargetOption(
    string DisplayName,
    string Description,
    Downtype Downtype,
    bool UseSerial)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// OTA 升级页 ViewModel（**公司 V0.9 `0x16` 拉模式**，整页重做）。
///
/// 三条可选通道（对应公司协议 `0x16` 会话目标 `DOWNTYPE`）：
/// 1. **基站（USB 自升级）**：`DOWNTYPE=0x00`，经基站 EP4 命令通道（`0xFF02` 无 RID，定长 64B 补零）；
/// 2. **笔端（串口直连）**：`DOWNTYPE=0x01`，经串口（`115200,N,8,1`，变长不补零）；
/// 3. **笔端（经基站无线 · B1 透传）**：`DOWNTYPE=0x01`，同样走 EP4 命令通道——PC 出片 22×48B/块，
///    基站只做搬运/流控，笔端自校验/切区/复位（B1 对 PC 无感，issues/09 总纲）。
///
/// 会话逻辑全部复用 Core 的 <see cref="CompanyOtaFsm"/>（拉模式：`0x0A` 请求 → 22×`0x07` → `0x20` 进度/终态；
/// 含 R1 块级重查 / R2 超时续等+最后手段（B1 冻结口径）/ R4 不悬挂 / R5 越界不补零 /
/// E23 `DOWNTYPE` 校验 / 末块按 `FILELEN` 截断）。
/// 本类只做：选通道 → 选文件 → 起会话 → 订阅进度/转移 → 展示。
/// </summary>
public partial class OtaPageViewModel : ObservableObject
{
    private readonly ICompanyCodec _codec;
    private readonly IClock _clock;
    private readonly CompanyConfig _config;
    private readonly HidCommandChannel _usbChannel;
    private readonly SerialCommandChannel _serialChannel;
    private readonly ISerialTransport _serialTransport;
    private readonly ILogger<OtaPageViewModel> _logger;
    private CancellationTokenSource? _upgradeCts;

    public OtaPageViewModel(
        ICompanyCodec codec,
        IClock clock,
        CompanyConfig config,
        HidCommandChannel usbChannel,
        SerialCommandChannel serialChannel,
        ISerialTransport serialTransport,
        ILogger<OtaPageViewModel> logger)
    {
        _codec = codec;
        _clock = clock;
        _config = config;
        _usbChannel = usbChannel;
        _serialChannel = serialChannel;
        _serialTransport = serialTransport;
        _logger = logger;

        foreach (var opt in OtaTargets)
            TargetOptions.Add(opt);
        _selectedTarget = OtaTargets[0];
        Hver = RecommendedHver(_selectedTarget);   // 首屏即与"当前轨"的推荐 HVER 一致

        _ = LoadSerialPortsAsync();
    }

    /// <summary>可选升级通道（三条：丙 基站自升级 / 乙-串口 笔端直连 / 乙-B1 笔端经基站无线透传）</summary>
    public static readonly OtaTargetOption[] OtaTargets =
    {
        new("基站（USB 自升级）", "经基站 EP4 命令通道写入基站固件（DOWNTYPE=0x00）", Downtype.BaseStation, UseSerial: false),
        new("笔端（串口直连）", "经串口直连笔端写入笔端固件（DOWNTYPE=0x01）", Downtype.Pen, UseSerial: true),
        new("笔端（经基站无线·B1 透传）", "经基站 BLE 透传写入笔端固件（DOWNTYPE=0x01；PC 出片 22×48B/块，基站只搬运+流控，笔端自校验/切区/复位）",
            Downtype.Pen, UseSerial: false)
    };

    /// <summary>该轨推荐 <c>HVER</c>：按**升级对象**（Downtype）取——笔端=6（R1 V1.0 板）、基站=1</summary>
    private byte RecommendedHver(OtaTargetOption t) =>
        t.Downtype == Downtype.Pen ? _config.HverPen : _config.HverHid;

    public ObservableCollection<OtaTargetOption> TargetOptions { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSerialTarget))]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(StageNote))]
    private OtaTargetOption _selectedTarget;

    /// <summary>当前通道是否走串口（决定是否显示串口选择行）</summary>
    public bool IsSerialTarget => SelectedTarget.UseSerial;

    /// <summary>
    /// 当前通道的**阶段提示**（各轨真实能力不同，必须区分，避免把"协议面完成"误当"固件已升级"）：
    /// - 基站轨：已接真写 flash / 切 bank → 完成后设备复位并运行包内固件；
    /// - 笔端·串口轨：写区/激活仍为 STUB → 版本不变、不复位；
    /// - 笔端·B1 透传轨：完成后笔端自校验（整包 CRC16）→ 切区 → 复位（BLE 重新广播）。
    /// </summary>
    public string StageNote => SelectedTarget switch
    {
        { UseSerial: true } =>
            "⚠ 笔端·串口直连（当前阶段）：写区/激活仍为 STUB —— 收到【完成】只代表协议面成功，设备版本不会改变、也不会复位；请以【读设备版本】为准。",
        { Downtype: Downtype.Pen } =>
            "笔端·B1 透传：完成后笔端自校验（整包 CRC16）→ 切区 → 复位（BLE 重新广播）。前置：笔端须已与基站配对（未配对不进 DFU）。可用【读设备版本】（自动填 target=0x11 经基站读笔端）复核。",
        _ =>
            "⚠ 基站（当前阶段）：已接真写 flash / 切 bank —— 完成后设备将复位并运行包内固件；请用【读设备版本】核对版本是否变为目标版本（刻意降级时版本“倒退”同样是生效证据）。"
    };

    /// <summary>
    /// 切换升级通道时：① 版本显示复位（已不对应当前设备）；② HVER 自动切到该轨推荐值
    /// （按**升级对象**取：笔端 R1 V1.0 = `6`、基站 = `1`）——避免"换轨忘记改 HVER"导致开始帧被拒（`FF`+原因 `01`）。
    /// </summary>
    partial void OnSelectedTargetChanged(OtaTargetOption value)
    {
        DeviceVersionText = "未读取";
        Hver = RecommendedHver(value);
    }

    // ============ 固件与版本 ============

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private string _firmwarePath = string.Empty;

    /// <summary>目标版本 `M.m.p`（须**异于**设备当前版本，否则设备回 `DCMD=0x02` 跳过）</summary>
    [ObservableProperty]
    private string _targetVersion = "1.0.0";

    /// <summary>
    /// 板级序号 `HVER`。**默认 `6`** = 笔端 R1 **V1.0** 板原值（`VP_BOARD_TYPE=6`）——
    /// 笔端需求 `06 §T7-0` 明确"必须 = 6，不符 → `DCMD=0xFF` + 原因 `0x01`"。
    /// 基站轨请填 `1`；填 `-1` 表示按升级对象自动取配置值。
    /// </summary>
    [ObservableProperty]
    private int _hver = 6;

    [ObservableProperty]
    private string _imageSummary = "未选择固件";

    // ============ 设备版本核对（0x03，升级前判断 / 升级后复核） ============

    /// <summary>设备当前版本（读 `0x03` 所得）；用于"是否已同版本"预判与升级后复核</summary>
    [ObservableProperty]
    private string _deviceVersionText = "未读取";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReadDeviceVersion))]
    private bool _isReadingVersion;

    public bool CanReadDeviceVersion => !IsReadingVersion && !IsUpgrading;

    // ============ 串口（仅"笔端（串口直连）"通道需要） ============

    [ObservableProperty]
    private ObservableCollection<string> _serialPorts = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private string? _selectedSerialPort;

    [RelayCommand]
    private async Task LoadSerialPortsAsync()
    {
        try
        {
            var ports = await _serialTransport.EnumeratePortsAsync();
            var names = ports.Select(p => p.PortName).ToList();

            MainThreadHelper.EnsureOnMainThread(() =>
            {
                SerialPorts = new ObservableCollection<string>(names);
                if (string.IsNullOrWhiteSpace(SelectedSerialPort) && names.Count > 0)
                    SelectedSerialPort = names[0];
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate serial ports");
        }
    }

    // ============ 设备版本核对（0x03） ============

    /// <summary>
    /// 读**设备当前版本**（`0x03`）：按当前通道走 —— 串口＝笔端、USB＝基站。
    /// 用途：① 升级前判断"是否已同版本"（相同则设备会回 `DCMD=02` 跳过，不进传输）；
    /// ② 升级后复核（第 4 轮真写 Flash 后才会变化；STUB 阶段不变属预期）。
    /// </summary>
    [RelayCommand]
    private async Task ReadDeviceVersionAsync()
    {
        try
        {
            IsReadingVersion = true;
            DeviceVersionText = "读取中...";

            ICommandChannel channel;
            if (SelectedTarget.UseSerial)
            {
                var ch = await EnsureSerialChannelAsync();
                if (ch == null)
                {
                    DeviceVersionText = "串口未就绪：请先选择并确认笔端串口";
                    return;
                }
                channel = ch;
            }
            else
            {
                if (!_usbChannel.IsOpen)
                {
                    DeviceVersionText = "基站未连接：请先在仪表盘连接接收器 USB 命令通道（0xFF02）";
                    return;
                }
                channel = _usbChannel;
            }

            // G14（冻结，issues/05 §1-D / 红线 12）：串口轨/基站 target 恒 0x00；
            // **B1 透传轨读笔端版本须显式填笔端类别**（否则笔端不答），且无应答 ≠ 版本一致。
            byte targetByte = 0x00;
            string viaNote = string.Empty;
            if (!SelectedTarget.UseSerial && SelectedTarget.Downtype == Downtype.Pen)
            {
                targetByte = _config.PenTargetClass;
                viaNote = $"（target=0x{targetByte:X2} 经基站透传读笔端）";
            }

            var requester = new CommandRequester(_clock, _config);
            var resp = await requester.RequestAsync(channel, _codec.EncodeReadVersion(targetByte), CompanyCmd.ReadVersion);

            if (resp == null)
            {
                DeviceVersionText = "0x03 无应答（超时）" +
                    (targetByte != 0 ? "：可能是 target 不匹配 / 笔端未与基站连接（无应答 ≠ 版本一致）" : "：请确认接线/设备状态");
                AddLog($"[0x03] 无应答（超时）{viaNote}");
                return;
            }

            if (!_codec.TryParseVersionResp(resp, out var info))
            {
                DeviceVersionText = "0x03 应答结构非法";
                return;
            }

            if (info.Status != CompanyStatus.Ok)
            {
                DeviceVersionText = $"0x03 失败：STATUS=0x{info.Status:X2}";
                AddLog($"[0x03] 失败：STATUS=0x{info.Status:X2}");
                return;
            }

            var sver = $"{info.SverMajor}.{info.SverMinor}.{info.SverPatch}";
            var sameAsTarget = string.Equals(sver, TargetVersion.Trim(), StringComparison.OrdinalIgnoreCase);

            // G14 防错闸（红线 12 配套）：B1 轨请求笔端类别但应答 MODEL 类别段不符 ⇒ 基站自答冒充
            // （基站固件未按 target 过滤/透传——2026-09-17 真机实测复现）→ 明示"不是笔端版本"
            if (targetByte != 0x00 && (info.Model & 0xF0) != (targetByte & 0xF0))
            {
                DeviceVersionText = $"⚠ 应答类别不符：请求 target=0x{targetByte:X2}（笔端）但 MODEL=0x{info.Model:X2}（基站自答）" +
                                    "——疑似基站固件未实现 G14 过滤，读到的不是笔端版本";
                AddLog($"[0x03] ⚠ 基站自答冒充（MODEL=0x{info.Model:X2} ≠ 笔端类别）：SVER={sver} 不可信{viaNote}");
                return;
            }

            DeviceVersionText = $"MODEL=0x{info.Model:X2}　SVER={sver}" +
                                (sameAsTarget ? "（与目标版本相同 ⇒ 设备会回 DCMD=02 跳过传输）" : string.Empty);

            AddLog($"[0x03] MODEL=0x{info.Model:X2} SVER={sver}{viaNote}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "0x03 read failed");
            DeviceVersionText = $"读版本失败：{ex.Message}";
        }
        finally
        {
            IsReadingVersion = false;
        }
    }

    // ============ 固件选择 ============

    [RelayCommand]
    private async Task BrowseFirmwareAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "固件文件 (*.bin)|*.bin|所有文件 (*.*)|*.*",
            Title = "选择固件文件（.bin 原始镜像）"
        };

        if (dlg.ShowDialog() != true)
            return;

        FirmwarePath = dlg.FileName;

        // 从文件名推断目标版本（如 `pen_r1_v10_v0.11.16_target_*.bin` → `0.11.16`），减少手填错
        var inferred = TryParseVersionFromFileName(dlg.FileName);
        if (inferred != null)
        {
            TargetVersion = inferred;
            AddLog($"[FILE] 已从文件名推断目标版本：{TargetVersion}（请确认与固件自身版本一致）");
        }

        try
        {
            var image = await File.ReadAllBytesAsync(dlg.FileName);
            var crc = Crc16CcittFalse.Compute(image);
            var blocks = (image.Length + 1023) / 1024;

            var overLimit = image.Length > _config.MaxImageBytes
                ? $"　⚠ 超出设备上限 {_config.MaxImageBytes} B（设备会回 FF + 原因 02）"
                : string.Empty;

            ImageSummary = $"{image.Length} 字节 / {blocks} 块　CRC16=0x{crc:X4}{overLimit}";
            AddLog($"[FILE] {dlg.FileName}（{image.Length}B, CRC16=0x{crc:X4}, {blocks} 块）");
        }
        catch (Exception ex)
        {
            ImageSummary = "读取固件失败";
            AddLog($"[ERR ] 读取固件失败：{ex.Message}");
        }
    }

    /// <summary>从固件文件名推断版本号（`v0.11.16` / `v1.2.4` → `M.m.p`；无则 null）</summary>
    private static string? TryParseVersionFromFileName(string path)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            System.IO.Path.GetFileName(path),
            @"v(\d+)\.(\d+)\.(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return match.Success
            ? $"{int.Parse(match.Groups[1].Value)}.{int.Parse(match.Groups[2].Value)}.{int.Parse(match.Groups[3].Value)}"
            : null;
    }

    // ============ 升级控制 ============

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(CanReadDeviceVersion))]
    private bool _isUpgrading;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _progressText = "空闲";

    [ObservableProperty]
    private string _statusMessage = "选择升级通道与固件后开始";

    public bool CanStart =>
        !IsUpgrading
        && !string.IsNullOrWhiteSpace(FirmwarePath)
        && File.Exists(FirmwarePath)
        && (!SelectedTarget.UseSerial || !string.IsNullOrWhiteSpace(SelectedSerialPort));

    [RelayCommand]
    private async Task StartUpgradeAsync()
    {
        if (!CanStart)
        {
            StatusMessage = "请先选择固件" + (SelectedTarget.UseSerial ? "与笔端串口" : "并确认基站已连接");
            return;
        }

        // 校验版本格式（M.m.p）
        var parts = TargetVersion.Split('.',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts.Any(p => !byte.TryParse(p, out _)))
        {
            StatusMessage = "目标版本格式非法：应为 M.m.p（如 1.0.0）";
            return;
        }

        try
        {
            IsUpgrading = true;
            ProgressPercent = 0;
            ProgressText = "准备中...";

            var image = await File.ReadAllBytesAsync(FirmwarePath);

            // 选通道并按形态就绪（USB：EP4 命令通道；串口：打开笔端串口，不施加 DTR/RTS）
            ICommandChannel channel;
            if (SelectedTarget.UseSerial)
            {
                channel = await EnsureSerialChannelAsync() ?? throw new InvalidOperationException("串口通道未就绪");
            }
            else
            {
                if (!_usbChannel.IsOpen)
                    throw new InvalidOperationException("基站未连接：请先在仪表盘连接接收器 USB 命令通道（0xFF02）");
                channel = _usbChannel;
            }

            var downtype = SelectedTarget.Downtype;
            var hver = (byte)(Hver >= 0
                ? Hver
                : (downtype == Downtype.Pen ? _config.HverPen : _config.HverHid));

            var fsm = new CompanyOtaFsm(_codec, channel, _clock, _config);
            fsm.Transitioned += (_, t) => MainThreadHelper.EnsureOnMainThread(() =>
            {
                AddLog($"[FSM] {t.From} → {t.To}：{t.Reason}");
                ProgressText = t.To.ToString();
            });
            fsm.Progress += (_, p) => MainThreadHelper.EnsureOnMainThread(() =>
            {
                // 进度以**设备侧**为准（设备是权威；自算仅兜底 —— 红线 4：自算不替代终态）
                ProgressPercent = p.DevicePercent >= 0 ? p.DevicePercent : p.Percent;
                AddLog($"[OTA] 进度 {ProgressPercent}%（{p.BlocksDone}/{p.TotalBlocks} 块；设备侧 {p.DevicePercent}%）");
            });
            fsm.Warning += (_, w) => MainThreadHelper.EnsureOnMainThread(() => AddLog($"[WARN] {w}"));

            var crc = Crc16CcittFalse.Compute(image);
            AddLog($"[START] 通道={SelectedTarget.DisplayName} 目标={downtype}(0x{(byte)downtype:X2}) " +
                   $"镜像={image.Length}B CRC16=0x{crc:X4} HVER=0x{hver:X2} SVER={TargetVersion}");

            _upgradeCts = new CancellationTokenSource();
            OtaSessionActivity.InProgress = true;   // C5：升级会话在位 → 0x54 信封命令本地让路，终态后补发
            var result = await fsm.RunAsync(image, downtype, hver,
                byte.Parse(parts[0]), byte.Parse(parts[1]), byte.Parse(parts[2]),
                cancellationToken: _upgradeCts.Token);

            ProgressPercent = result.IsTerminalSuccess ? 100 : ProgressPercent;

            if (result.IsTerminalSuccess)
            {
                // ⚠️ 阶段③：设备侧写区/激活/复位仍为 STUB ⇒ DCMD=02 只代表"协议面成功"
                var note = result.Outcome == OtaOutcome.Skipped
                    ? "（设备已同版本，未进入传输）"
                    : "；协议面成功 —— 请点【读设备版本】核对（STUB 阶段设备不会复位、版本不变，属预期）";

                StatusMessage = $"{result.Message}（{result.Outcome}）{note}";
                AddLog($"[DONE] {result.Message}{note}");
            }
            else
            {
                StatusMessage = $"升级未完成：{result.Message}（{result.Outcome}）";
                AddLog($"[FAIL] {result.Message}（{result.Outcome}）");
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "已取消";
            AddLog("[CANCEL] 用户取消");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OTA failed");
            StatusMessage = $"升级失败：{ex.Message}";
            AddLog($"[ERR ] {ex.Message}");
        }
        finally
        {
            OtaSessionActivity.InProgress = false;   // 会话终态（成功/失败/取消/异常一律）→ 解除 0x54 让路
            IsUpgrading = false;
            _upgradeCts?.Dispose();
            _upgradeCts = null;
        }
    }

    [RelayCommand]
    private void CancelUpgrade()
    {
        _upgradeCts?.Cancel();
        StatusMessage = "正在取消...";
    }

    /// <summary>确保笔端串口通道就绪（端口不同则重连；`115200,N,8,1`，不施加 DTR/RTS）</summary>
    private async Task<ICommandChannel?> EnsureSerialChannelAsync()
    {
        var port = SelectedSerialPort!;

        if (!_serialTransport.IsConnected ||
            !string.Equals(_serialTransport.CurrentPortName, port, StringComparison.OrdinalIgnoreCase))
        {
            var settings = new SerialPortSettings { BaudRate = _config.SerialBaudRate };
            if (!await _serialTransport.ConnectAsync(port, settings))
            {
                AddLog($"[ERR ] 打开串口失败：{port}");
                return null;
            }
            AddLog($"[PORT] 已打开 {port} @ {_config.SerialBaudRate}（不施加 DTR/RTS）");
        }

        return _serialChannel;
    }

    // ============ 日志 ============

    public ObservableCollection<string> LogEntries { get; } = new();

    [RelayCommand]
    private void ClearLog()
    {
        LogEntries.Clear();
        StatusMessage = "日志已清空";
    }

    private void AddLog(string entry)
    {
        if (LogEntries.Count > 1000)
        {
            for (int i = 0; i < 100; i++)
                LogEntries.RemoveAt(0);
        }
        LogEntries.Add($"{DateTime.Now:HH:mm:ss.fff} {entry}");
    }
}
