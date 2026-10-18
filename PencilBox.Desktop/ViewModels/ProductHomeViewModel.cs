// <copyright file="ProductHomeViewModel.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PencilBox.Core.Ble;
using PencilBox.Core.Communication;
using PencilBox.Core.Fsm;
using PencilBox.Core.Models;
using PencilBox.Core.Protocol;
using PencilBox.Core.Protocol.Company;
using PencilBox.Core.Services;
using PencilBox.Desktop.Helpers;
using PencilBox.Desktop.Views;

namespace PencilBox.Desktop.ViewModels;

/// <summary>
/// 产品主页 ViewModel（默认首页）
/// 专属产品控制面板：设备连接 + 笔端状态（电池/信号）+ Keymap 表格管理。
/// </summary>
public partial class ProductHomeViewModel : ObservableObject
{
    private readonly IDeviceService _deviceService;
    private readonly IPencilStateService _pencilStateService;
    private readonly IKeymapConfigStore _keymapConfigStore;
    private readonly IPenBindingService _penBindingService;
    private readonly IDongleCommandClient _dongle;
    private readonly ISerialTransport _serialTransport;
    private readonly CompanyPenStatusService _penStatus;
    private readonly CompanyKeymapConfigService _keymapConfig;
    private readonly IGlobalKeyboardHook _keyboardHook;
    private readonly IVpCommandService _vpCmd;
    private readonly ILogger<ProductHomeViewModel> _logger;

    private CancellationTokenSource? _pollCts;
    private CancellationTokenSource? _penPushCts;

    // ============ 设备连接 ============
    [ObservableProperty]
    private ObservableCollection<HidDeviceInfo> _availableDevices = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    private HidDeviceInfo? _selectedDevice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    [NotifyPropertyChangedFor(nameof(CanUseK2))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    private bool _isConnecting;

    [ObservableProperty]
    private DeviceInfo? _dongleInfo;

    /// <summary>Dongle 配对状态（A1 GET_STATE）文本，连接后自动/手动刷新</summary>
    [ObservableProperty]
    private string _donglePairText = "未连接：配对状态不可用";

    /// <summary>是否有配对状态（已连接并收到 A1 应答）</summary>
    [ObservableProperty]
    private bool _hasPairState;

    [ObservableProperty]
    private string _statusMessage = "请扫描并连接接收器（Dongle）";

    public bool CanConnect => SelectedDevice != null && !IsConnected && !IsConnecting;

    // ============ 笔端绑定（**两条来源、一份存储**） ============
    // 统一原则：BLE 扫描与串口 0x51 读目的完全相同（拿到笔端 MAC），都汇入同一份 KnownPen 目录
    // （显示序 12HEX 单源）；因此"下发给基站"只有一条路径（0x51 写，显示序直传、PC 零转换）。
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPenTargets))]
    private ObservableCollection<KnownPen> _penTargets = new();

    /// <summary>目录是否有记录（控制"清除记录"按钮可用态）</summary>
    public bool HasPenTargets => PenTargets.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBind))]
    [NotifyPropertyChangedFor(nameof(SelectedPenDisplayMac))]
    [NotifyPropertyChangedFor(nameof(SelectedPenSummary))]
    private KnownPen? _selectedPen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBind))]
    private bool _isScanningBle;

    [ObservableProperty]
    private bool _isBinding;

    // ---- 来源②：串口直连笔端读 MAC（公司协议 0x51） ----
    [ObservableProperty]
    private ObservableCollection<string> _serialPorts = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReadPenBySerial))]
    [NotifyPropertyChangedFor(nameof(CanClearBinding))]
    private string? _selectedSerialPort;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReadPenBySerial))]
    private bool _isReadingPenBySerial;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanClearBinding))]
    private bool _isClearingBinding;

    public bool CanBind => SelectedPen != null && !IsBinding;

    public bool CanReadPenBySerial =>
        !string.IsNullOrWhiteSpace(SelectedSerialPort) && !IsReadingPenBySerial;

    /// <summary>笔端清绑定可用态（已选串口且无在途会话；仅串口直达路径）</summary>
    public bool CanClearBinding =>
        !string.IsNullOrWhiteSpace(SelectedSerialPort) && !IsClearingBinding;

    /// <summary>选中笔端的显示序 MAC（= 下发值；PC 零转换，原样进 `0x51` 写）</summary>
    public string? SelectedPenDisplayMac => SelectedPen?.MacDisplayText;

    /// <summary>选中笔端摘要：来源 + 协议 `BT_NAME`（确认展示）</summary>
    public string? SelectedPenSummary => SelectedPen == null
        ? null
        : $"来源：{SelectedPen.SourceName}　下发名：{SelectedPen.EffectiveBtName}";

    // ============ 笔端状态 ============
    [ObservableProperty]
    private int _batteryLevel;

    [ObservableProperty]
    private int _rssi;

    [ObservableProperty]
    private bool _pencilConnected;

    [ObservableProperty]
    private string _penChargeStateText = "—";

    /// <summary>电池环中心充电状态文字（"充电中"/"已充满"；空串=未充电不占位，% 保持可见）</summary>
    [ObservableProperty]
    private string _gaugeChargeText = "";

    /// <summary>电池环中心充电状态颜色（充电中橙 / 已充满绿）</summary>
    [ObservableProperty]
    private System.Windows.Media.Brush _gaugeChargeBrush = System.Windows.Media.Brushes.Gray;

    /// <summary>电池环 % 数值颜色：充电中灰显=读数虚高仅供参考（数值保持可见），其余正常深灰</summary>
    [ObservableProperty]
    private System.Windows.Media.Brush _gaugeValueBrush = NormalValueBrush;

    private static readonly System.Windows.Media.Brush ChargingBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE6, 0x7E, 0x22));

    private static readonly System.Windows.Media.Brush ChargeFullBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2E, 0xCC, 0x71));

    private static readonly System.Windows.Media.Brush NormalValueBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x33, 0x33, 0x33));

    private static readonly System.Windows.Media.Brush DimmedValueBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x99, 0x99, 0x99));

    /// <summary>连接状态文本（A1 驱动）：已连接 / 已配对·未连接 / 未配对 / 未知</summary>
    [ObservableProperty]
    private string _pencilStateText = "未连接";

    /// <summary>电量/信号是否可用（0x53 状态上行已通即亮卡；RSSI 仍待 0x42）</summary>
    [ObservableProperty]
    private bool _hasPenTelemetry;

    /// <summary>笔端设备状态（0x53 DEV_STATE 槽）：正常 / 休眠</summary>
    [ObservableProperty]
    private string _penDevStateText = "—";

    /// <summary>0x53 推送监听开关态（连笔/槽变化/断连实时上卡）</summary>
    [ObservableProperty]
    private bool _isPenPushListening;

    /// <summary>0x53 最近一帧的来源/元信息（拉取时间戳或推送序号）</summary>
    [ObservableProperty]
    private string _penStatusMetaText = "未获取（连接后点「刷新笔端状态」）";

    /// <summary>笔端工作状态（自管两轴展示，产品落地 §三；"待事件"=未知，边界事件/断连自愈）</summary>
    [ObservableProperty]
    private string _workStateText = "待事件";

    /// <summary>工作状态机（识别 × 扩音档两轴；触发键绑定跟随「已应用」配置）</summary>
    private PenWorkStateTracker _workStateTracker = null!;

    /// <summary>触发键绑定跟随的「已应用」配置（生效回执成功才推进；仅选择配置不推进——回执门控）</summary>
    private string _appliedKeymapName = "";

    // ============ K2 启动方式（0x54 信封 0x21/0x42，issues/08 M4 UI）============
    [ObservableProperty]
    private int _k2ModeIndex;

    [ObservableProperty]
    private string _k2ModeResultText = "未查询：应用/查询经 0x54 信封 → 基站 → 当前连接笔端";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseK2))]
    private bool _isK2Busy;

    /// <summary>K2 卡片可用态（USB 命令通道已连且无在途信封事务）</summary>
    public bool CanUseK2 => IsConnected && !IsK2Busy;

    // ============ Keymap ============
    [ObservableProperty]
    private ObservableCollection<KeymapRowViewModel> _keymapRows = new();

    /// <summary>S1 档位切换快捷键（随命名配置携带，旧配置回落出厂真值；档位锁定、键值可编辑；设备写回待 0x55 PARAM 05/06）</summary>
    [ObservableProperty]
    private ObservableCollection<KeymapSwitchRowViewModel> _switchRows = new();

    /// <summary>一键一卡视图（卡=物理键，行=事件；行实例与 <see cref="KeymapRows"/> 共享，编辑互通）</summary>
    [ObservableProperty]
    private ObservableCollection<KeymapCardViewModel> _keymapCards = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyKeymapConfigCommand))]
    private bool _isKeymapDirty;

    // 命名配置（pc-issues/04 提案 B：configs/keymap/ 一配置一 JSON；出厂默认只读）
    [ObservableProperty]
    private ObservableCollection<string> _keymapConfigNames = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFactoryConfigSelected))]
    private string? _selectedKeymapConfig;

    /// <summary>当前选中是否出厂默认（只读）——驱动保存/改名/删除按钮与表格可编辑性</summary>
    public bool IsFactoryConfigSelected =>
        string.Equals(SelectedKeymapConfig, _keymapConfigStore.FactoryName, StringComparison.Ordinal);

    partial void OnSelectedKeymapConfigChanged(string? value)
    {
        IsKeymapDirty = false;
        try { _keymapConfigStore.SetCurrentName(value ?? _keymapConfigStore.FactoryName); }
        catch (Exception ex) { _logger.LogWarning(ex, "记录当前 Keymap 配置失败"); }
        LoadSelectedConfigIntoCards();
        NotifyKeymapMutateCommands();
    }

    private void NotifyKeymapMutateCommands()
    {
        SaveKeymapConfigCommand.NotifyCanExecuteChanged();
        RenameKeymapConfigCommand.NotifyCanExecuteChanged();
        DeleteKeymapConfigCommand.NotifyCanExecuteChanged();
        ApplyKeymapConfigCommand.NotifyCanExecuteChanged();
    }

    public ProductHomeViewModel(
        IDeviceService deviceService,
        IPencilStateService pencilStateService,
        IKeymapConfigStore keymapConfigStore,
        IPenBindingService penBindingService,
        IDongleCommandClient dongle,
        ISerialTransport serialTransport,
        CompanyPenStatusService penStatus,
        CompanyKeymapConfigService keymapConfig,
        IGlobalKeyboardHook keyboardHook,
        IVpCommandService vpCmd,
        ILogger<ProductHomeViewModel> logger)
    {
        _deviceService = deviceService;
        _pencilStateService = pencilStateService;
        _keymapConfigStore = keymapConfigStore;
        _penBindingService = penBindingService;
        _dongle = dongle;
        _serialTransport = serialTransport;
        _penStatus = penStatus;
        _keymapConfig = keymapConfig;
        _keyboardHook = keyboardHook;
        _vpCmd = vpCmd;
        _logger = logger;

        _deviceService.ConnectionStateChanged += OnConnectionStateChanged;
        _deviceService.DeviceListChanged += OnDeviceListChanged;
        _deviceService.DeviceInfoChanged += OnDeviceInfoChanged;
        _pencilStateService.StateUpdated += OnPencilStateUpdated;

        // 命名 Keymap 配置（pc-issues/04 提案 A/B）：确保目录/出厂默认在位 + 旧缓存迁移，载入当前配置。
        // 失败不阻塞启动（表空可后续手动操作）。
        try
        {
            _keymapConfigStore.Initialize();
            RefreshKeymapConfigNames();
            SelectedKeymapConfig = _keymapConfigStore.GetCurrentName();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Keymap 配置存储初始化失败");
        }

        // 笔端工作状态自管（产品落地 §三，2026-10-18）：全局键盘钩子被动监听（不吞键）。
        // 触发键绑定跟随「已下发基站且回执成功」的配置（回执门控，2026-10-18 定版）；
        // 启动时以上次记录的当前配置为最优近似（固件真值要等 0x55 读侧落地才可对账）。
        _appliedKeymapName = SelectedKeymapConfig ?? _keymapConfigStore.FactoryName;
        _workStateTracker = BuildWorkStateTracker(_appliedKeymapName);
        WorkStateText = _workStateTracker.DisplayText;
        _keyboardHook.KeyDown += OnWorkStateKeyDown;
        _keyboardHook.Start();

        // S1 档位切换表：出厂真值兜底显示（配置初始化成功后由 LoadSelectedConfigIntoCards 按配置重载）
        SwitchRows = new ObservableCollection<KeymapSwitchRowViewModel>(
            KeymapFactoryDefault.SwitchEntries
                .Select(e => new KeymapSwitchRowViewModel(e, isEditable: false))
                .OrderBy(r => r.Mode));

        // 启动时加载本地已知笔端目录（显示"上次使用/使用过/出现过"历史）
        _ = LoadKnownPensAsync();

        // 启动时枚举可用串口（供"串口读笔端"这条来源直接选）
        _ = LoadSerialPortsAsync();
    }

    // ============ 设备连接命令 ============

    [RelayCommand]
    private async Task ScanDevicesAsync()
    {
        try
        {
            StatusMessage = "正在扫描设备...";
            var devices = await _deviceService.ScanDevicesAsync();
            MainThreadHelper.EnsureOnMainThread(() =>
            {
                // 三层：无关设备过滤掉 → 只留本产品设备（浅黄底）→ 命令接口 0xFF02（★）排最前并选中
                var ours = devices.Where(d => d.IsOurDevice).ToList();
                var ordered = ours.OrderByDescending(d => d.IsCommandChannel).ToList();
                AvailableDevices = new ObservableCollection<HidDeviceInfo>(ordered);

                var target = ordered.FirstOrDefault(d => d.IsCommandChannel);
                if (target != null)
                {
                    SelectedDevice = target;
                    StatusMessage = $"已识别接收器命令通道：{target.ProductName}（VID_0x{target.VendorId:X4} PID_0x{target.ProductId:X4}）";
                }
                else if (ordered.Count > 0)
                {
                    SelectedDevice = ordered[0];
                    StatusMessage = "已发现本产品设备，但未识别到 0xFF02 命令通道接口";
                }
                else
                {
                    StatusMessage = "未发现本产品设备（VID 0x2F70/0xEA11），请确认接收器已插入";
                }
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"扫描失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (SelectedDevice == null)
            return;

        try
        {
            IsConnecting = true;
            StatusMessage = $"正在连接 {SelectedDevice.ProductName}...";

            var ok = await _deviceService.ConnectAsync(SelectedDevice.DevicePath);

            MainThreadHelper.EnsureOnMainThread(() =>
            {
                IsConnecting = false;
                if (ok)
                {
                    StatusMessage = "已连接，正在同步状态与 Keymap...";
                    _ = RefreshAfterConnectAsync();
                }
                else
                {
                    StatusMessage = "连接失败";
                }
            });
        }
        catch (Exception ex)
        {
            MainThreadHelper.EnsureOnMainThread(() =>
            {
                IsConnecting = false;
                StatusMessage = $"连接错误: {ex.Message}";
            });
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        StopPolling();
        StopPenPushLoop(null);
        await _deviceService.DisconnectAsync();
        StatusMessage = "已断开";
        PencilConnected = false;
        BatteryLevel = 0;
        Rssi = 0;
        HasPenTelemetry = false;
        PenDevStateText = "—";
        ClearChargeDisplay();
        IsPenPushListening = false;
        PenStatusMetaText = "未获取（连接后点「刷新笔端状态」）";
        K2ModeResultText = "未查询：应用/查询经 0x54 信封 → 基站 → 当前连接笔端";
    }

    // ============ 笔端绑定命令 ============

    [RelayCommand]
    private async Task ScanPensAsync()
    {
        try
        {
            IsScanningBle = true;
            StatusMessage = "正在扫描笔端蓝牙广播（并入本地目录）...";

            var pens = (await _penBindingService.ScanPensAsync(5)).ToList();

            MainThreadHelper.EnsureOnMainThread(() =>
            {
                PenTargets = new ObservableCollection<KnownPen>(pens);
                IsScanningBle = false;

                if (pens.Count == 0)
                {
                    SelectedPen = null;
                    StatusMessage = "目录为空：未发现 SunVote 笔端。请确认笔已上电 / 已进入配对视窗（HOME+UP 2s）后再次扫描";
                    return;
                }

                // 目录已按"上次使用>使用过>出现过"、层内在场+信号排序；默认选中首个（最优先）
                SelectedPen = pens[0];

                // 区分"本次扫描在线"与"目录总数"：笔端 USB 连线中 / 已被基站连接时**不广播**
                // （BLE 从机被连即停广播，属正常行为）——不算扫描链路故障，但要给出可判读的提示。
                var present = pens.Count(p => p.IsPresent);
                StatusMessage = present > 0
                    ? $"本次扫描在线 {present} 支 / 目录共 {pens.Count} 支，已选中：{pens[0].MacDisplayText}（{pens[0].LevelName}）"
                    : $"本次扫描在线 0 支（目录 {pens.Count} 支为历史记录）。提示：笔端 USB 连线中或已被基站连接时不会广播，拔线/断开后重扫";
            });
        }
        catch (Exception ex)
        {
            MainThreadHelper.EnsureOnMainThread(() =>
            {
                IsScanningBle = false;
                StatusMessage = $"扫描失败: {ex.Message}";
            });
        }
    }

    /// <summary>清空本地已知笔端目录（出现/使用记录全部抹掉；不影响已下发到基站的绑定）</summary>
    [RelayCommand]
    private async Task ClearPenTargetsAsync()
    {
        try
        {
            var count = PenTargets.Count;
            await _penBindingService.ClearKnownPensAsync();

            MainThreadHelper.EnsureOnMainThread(() =>
            {
                PenTargets = new ObservableCollection<KnownPen>();
                SelectedPen = null;
                StatusMessage = count > 0
                    ? $"已清除全部 {count} 条笔端记录（不影响基站已下发的绑定）"
                    : "目录本就为空，无需清除";
            });
        }
        catch (Exception ex)
        {
            MainThreadHelper.EnsureOnMainThread(() =>
            {
                StatusMessage = $"清除记录失败: {ex.Message}";
            });
        }
    }

    // ---- 来源②：串口直连笔端读 MAC（公司协议 0x51） ----

    /// <summary>刷新可用串口列表（笔端串口：115200,N,8,1；不施加 DTR/RTS）</summary>
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

    /// <summary>
    /// **串口直连笔端读 MAC**（公司协议 `0x51`）→ 并入**同一份**笔端目录。
    /// 与"扫描笔端"（BLE）只是取 MAC 的两条路径；产物同构、与来源无关，
    /// 之后"绑定"走同一条 `0x51` 下发（显示序直传、PC 零转换）。
    /// </summary>
    [RelayCommand]
    private async Task ReadPenBySerialAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedSerialPort))
        {
            StatusMessage = "请先选择笔端串口（如 COM20）";
            return;
        }

        try
        {
            IsReadingPenBySerial = true;
            StatusMessage = $"正在经串口 {SelectedSerialPort} 读笔端（0x51）...";

            var pens = (await _penBindingService.ScanPenViaSerialAsync(SelectedSerialPort)).ToList();

            MainThreadHelper.EnsureOnMainThread(() =>
            {
                IsReadingPenBySerial = false;
                PenTargets = new ObservableCollection<KnownPen>(pens);

                var read = pens.FirstOrDefault(p => p.Source == PenMacSource.SerialRead) ?? pens.FirstOrDefault();
                if (read != null)
                    SelectedPen = read;

                StatusMessage = read != null
                    ? $"串口读到笔端 {read.MacDisplayText}（{read.EffectiveBtName}），已并入同一份目录"
                    : "串口读完成，但目录仍为空";
            });
        }
        catch (Exception ex)
        {
            MainThreadHelper.EnsureOnMainThread(() =>
            {
                IsReadingPenBySerial = false;
                StatusMessage = $"串口读笔端失败：{ex.Message}";
            });
        }
    }

    /// <summary>
    /// **笔端清绑定**（`0x51 PARAM=03`，协议补充 01）——**仅串口直达**：笔端删自身 bond →
    /// 断连 → 进配对名视窗（60s）。破坏性副作用，先弹确认；与 Dongle 侧 A3 清目标互不影响。
    /// </summary>
    [RelayCommand]
    private async Task ClearPenBindingAsync()
    {
        if (!CanClearBinding)
        {
            StatusMessage = "请先选择笔端串口（清绑定仅串口直达路径；BLE 透传不提供，防误清）";
            return;
        }

        var confirm = MessageBox.Show(
            $"将经串口 {SelectedSerialPort} 直连笔端发送清绑定（0x51/03）。\n\n" +
            "笔端将执行：删除自身蓝牙绑定 → 断开当前连接 → 进配对名学习视窗（SunVote-R1#，60 秒，不复位）。\n" +
            "该操作清除笔端本体的绑定信息（接收器 Dongle 侧目标不受影响）。\n\n确认执行？",
            "笔端清绑定确认（串口直达）", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        IsClearingBinding = true;
        try
        {
            StatusMessage = $"正在经串口 {SelectedSerialPort} 直达笔端发送清绑定（0x51/03）...";
            var status = await _penBindingService.ClearPenBindingAsync(SelectedSerialPort!);

            MainThreadHelper.EnsureOnMainThread(() =>
            {
                StatusMessage = status switch
                {
                    0x00 => "清绑定已受理（00，幂等）：笔端删 bond → 断连 → 配对视窗 60s；稍后「扫描笔端」可见 # 配对态广播，再续配对",
                    0xFF => "清绑定被拒（FF）：笔端升级会话进行中（笔端状态未受影响）；会话结束后重发即可",
                    _ => $"清绑定返回 STATUS=0x{status:X2}（未知值，以笔端口径为准）"
                };
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"清绑定失败：{ex.Message}";
        }
        finally
        {
            MainThreadHelper.EnsureOnMainThread(() => IsClearingBinding = false);
        }
    }

    /// <summary>启动时加载本地已知笔端目录（无需扫描即可展示历史，含"上次使用"）</summary>
    private async Task LoadKnownPensAsync()
    {
        try
        {
            var pens = (await _penBindingService.LoadKnownPensAsync()).ToList();
            MainThreadHelper.EnsureOnMainThread(() =>
            {
                if (pens.Count > 0)
                {
                    PenTargets = new ObservableCollection<KnownPen>(pens);
                    SelectedPen = pens[0];
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load known pens catalog");
        }
    }

    [RelayCommand]
    private async Task BindPenAsync()
    {
        if (SelectedPen == null)
        {
            StatusMessage = "请先扫描并选择要绑定的笔端";
            return;
        }

        if (!IsConnected)
        {
            StatusMessage = "请先连接接收器（Dongle）USB 接口，再绑定笔端";
            return;
        }

        try
        {
            IsBinding = true;
            StatusMessage = $"正在绑定笔端 {SelectedPen.MacDisplayText}...";

            var result = await _penBindingService.BindPenByMacAsync(SelectedPen);

            MainThreadHelper.EnsureOnMainThread(() =>
            {
                IsBinding = false;
                StatusMessage = result.Message;
                if (result.Success)
                    _ = RefreshPairStateAsync();   // 绑定成功 → 回读 A1 展示新目标
            });
        }
        catch (Exception ex)
        {
            MainThreadHelper.EnsureOnMainThread(() =>
            {
                IsBinding = false;
                StatusMessage = $"绑定失败: {ex.Message}";
            });
        }
    }

    [RelayCommand]
    private async Task PairByNameAsync()
    {
        if (!IsConnected)
        {
            StatusMessage = "请先连接接收器（Dongle）USB 接口，再进入名称配对";
            return;
        }

        try
        {
            IsBinding = true;
            StatusMessage = "正在让 Dongle 进入名称配对（匹配 SunVote-R1#）...";

            var result = await _penBindingService.StartNamePairingAsync();

            MainThreadHelper.EnsureOnMainThread(() =>
            {
                IsBinding = false;
                StatusMessage = result.Message;
                if (result.Success)
                    _ = RefreshPairStateAsync();   // 配对窗开启 → 回读 A1 模式变化
            });
        }
        catch (Exception ex)
        {
            MainThreadHelper.EnsureOnMainThread(() =>
            {
                IsBinding = false;
                StatusMessage = $"名称配对失败: {ex.Message}";
            });
        }
    }

    private async Task RefreshAfterConnectAsync()
    {
        try
        {
            // Dongle 配对状态（A1 GET_STATE）
            await RefreshPairStateAsync();
            // 笔端状态快照（0x53 拉管对齐：UI 打开/重连时主动拉一次）+ 推送监听（推管实时）
            await RefreshPenStatusAsync();
            StartPenPushLoop();
            // 实时读取笔端状态
            await _pencilStateService.GetPencilStateAsync();
            // Keymap：本地配置已于启动载入（pc-issues/04 提案 A/B）；设备读写守卫"暂未开通"，不发帧
            // 启动轮询
            StartPolling();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh after connect");
        }
    }

    // ============ Dongle 配对状态（EP4 A1） ============

    /// <summary>
    /// 查询 Dongle 配对状态（A1 GET_STATE）并刷新展示。
    /// 仅内部调用（连接后刷新 / 2s 轮询 / 绑定·配对后回读）；手动刷新按钮已于 2026-09-23 裁决摘除
    /// （设备侧 4A4C 已退役，对现行固件恒超时；遗留联调走 CLI `device ep4 send --cmd A1`，10 号 §4.2）。
    /// </summary>
    private async Task RefreshPairStateAsync()
    {
        if (!_deviceService.IsConnected)
        {
            DonglePairText = "未连接：配对状态不可用";
            HasPairState = false;
            return;
        }

        try
        {
            var reply = await _dongle.SendCommandAsync((byte)CommandId.PairGetState, timeoutMs: 1500);
            MainThreadHelper.EnsureOnMainThread(() =>
            {
                if (!reply.Received || reply.Status != 0x00)
                {
                    DonglePairText = reply.Received
                        ? $"读取配对状态失败：status=0x{reply.Status:X2}（{reply.StatusText}）"
                        : "查询配对状态超时（Dongle 无应答）";
                    HasPairState = false;
                    return;
                }

                var st = DonglePairState.Parse(reply.Payload);
                if (!st.Valid)
                {
                    DonglePairText = "应答格式异常（A1 载荷过短）";
                    HasPairState = false;
                    return;
                }

                // A1 是"已连接/配对"的真实数据源（0x42 Dongle 未实现前，连接态以 A1 为准）
                PencilConnected = st.IsConnected;
                PencilStateText = st.IsConnected ? "已连接" : (st.IsPaired ? "已配对·未连接" : "未配对");

                var sb = new System.Text.StringBuilder();
                sb.Append(st.IsPaired
                    ? $"● 已绑定目标：{st.MacDisplay}"
                    : "○ 未绑定目标（可绑定笔端或名称配对）");
                sb.Append($"   配对模式:{st.ModeText}");
                if (st.IsConnected)
                    sb.Append("   笔端已连接");
                else if (st.IsPaired)
                    sb.Append("   笔端未连接（出现即自动建联）");
                if (!string.IsNullOrEmpty(st.Name))
                    sb.Append($"   名称:{st.Name}");

                DonglePairText = sb.ToString();
                HasPairState = true;
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A1 query failed");
            DonglePairText = "查询配对状态失败";
            HasPairState = false;
        }
    }

    // ============ 状态查询 ============

    [RelayCommand]
    private async Task RefreshStateAsync()
    {
        if (!IsConnected)
        {
            StatusMessage = "请先连接接收器";
            return;
        }

        try
        {
            // 连接/配对态（A1，实时可靠）
            await RefreshPairStateAsync();

            // 笔端状态快照（0x53：电量/休眠/运行模式——基站已实现，实际数值来源）
            await RefreshPenStatusAsync();

            // 电量/信号（0x42；Dongle 未实现前返回失败 → RSSI 卡片保持占位）
            var state = await _pencilStateService.GetPencilStateAsync();
            MainThreadHelper.EnsureOnMainThread(() =>
            {
                HasPenTelemetry = state != null && state.Status == 0x00;
                if (HasPenTelemetry)
                    StatusMessage = "状态已刷新";
                else
                    StatusMessage = "连接/配对状态已刷新；电量与信号需 Dongle 实现 0x42(GET_PENCIL_STATE) 后可用";
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"刷新状态失败: {ex.Message}";
        }
    }

    // ============ 笔端状态上行（0x53，协议补充 05）============

    /// <summary>拉取笔端状态快照（0x53 拉：基站槽缓存即答）→ 上卡实际数值。</summary>
    [RelayCommand]
    private async Task RefreshPenStatusAsync()
    {
        if (!_penStatus.IsOpen)
        {
            StatusMessage = "请先连接接收器（USB 命令通道），再刷新笔端状态（0x53 仅 USB 基站轨）";
            return;
        }

        try
        {
            var snapshot = await _penStatus.PullAsync();
            MainThreadHelper.EnsureOnMainThread(() =>
            {
                if (snapshot == null)
                {
                    PenStatusMetaText = "0x53 无应答（基站旧固件对未知 CMDTYPE 静默丢弃）";
                    return;
                }
                ApplyPenStatus(snapshot, "拉取");
                StatusMessage = snapshot.Status == PenStatusStatus.Ok
                    ? "笔端状态已刷新（0x53 快照）"
                    : "0x53：笔未连接（基站答 STATUS=02；笔上电连接后推送/重拉即得）";
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"0x53 拉取失败: {ex.Message}";
        }
    }

    /// <summary>推送监听开关：开 = 后台循环只等 `E1 01 53`（连笔/槽变化/断连实时上卡）。</summary>
    [RelayCommand]
    private void TogglePenPushListen()
    {
        if (IsPenPushListening)
        {
            StopPenPushLoop("已停止 0x53 推送监听");
            return;
        }
        StartPenPushLoop();
    }

    private void StartPenPushLoop()
    {
        if (!_penStatus.IsOpen)
        {
            StatusMessage = "请先连接接收器，再开启 0x53 推送监听";
            return;
        }

        StopPenPushLoop(null);
        _penPushCts = new CancellationTokenSource();
        var token = _penPushCts.Token;
        IsPenPushListening = true;
        StatusMessage = "已开启 0x53 推送监听（连笔 / 电量·休眠变化 / 断连 实时上卡）";

        _ = Task.Run(async () =>
        {
            var pushes = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var snapshot = await _penStatus.ListenAsync(2000, token);
                    if (snapshot != null)
                    {
                        pushes++;
                        MainThreadHelper.EnsureOnMainThread(() =>
                            ApplyPenStatus(snapshot, $"推送 #{pushes} {DateTime.Now:HH:mm:ss}"));
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _logger.LogDebug(ex, "0x53 push listen error"); }
            }
        }, token);
    }

    private void StopPenPushLoop(string? message)
    {
        _penPushCts?.Cancel();
        _penPushCts?.Dispose();
        _penPushCts = null;
        IsPenPushListening = false;
        if (message != null)
            StatusMessage = message;
    }

    /// <summary>把一帧 `0x53` 快照应用到状态卡（实际数值来源：BYTE2 充电枚举 + 状态槽）。</summary>
    private void ApplyPenStatus(PenStatusSnapshot s, string source)
    {
        PenStatusMetaText = $"0x53 {source}　BYTE2=0x{s.Byte2:X2}（chg={s.ChargeState} src={s.Src}）";
        HasPenTelemetry = true;   // 0x53 已通：卡片亮起（RSSI 走 0x03 槽，旧基站固件无槽时沿 0x42 旧路）

        if (s.Status == PenStatusStatus.PenNotConnected)
        {
            PencilConnected = false;
            PencilStateText = "笔未连接";
            PenDevStateText = "—";
            ClearChargeDisplay();
            return;
        }
        if (s.Status != PenStatusStatus.Ok)
        {
            PenDevStateText = "—";
            return;   // 未知状态：元信息已带原始值，卡片不乱覆盖
        }

        PencilConnected = true;

        var batt = s.Find(PenStateSlotId.Batt);
        if (batt != null)
            BatteryLevel = batt.Value;

        // RSSI 槽 0x03（协议补充/05 §9，2026-10-18 采纳）：dBm 补码原值，触发线④ ≤5s 跟手；
        // 旧基站固件无此槽 → 不覆盖（保留 0x42 旧路值），避免把信号条打回 0
        if (s.RssiDbm is { } rssi)
            Rssi = rssi;

        var dev = s.Find(PenStateSlotId.DevState);
        // DEV_STATE 槽：0x00=正常 / 0x01=休眠（专业叫法，随统一TX §15.2）；未知值原样露出（新态前向兼容，不误标正常）
        PenDevStateText = dev == null ? "—" : dev.Value switch
        {
            PenDevState.Normal => "正常",
            PenDevState.Sleep => "休眠",
            _ => $"未知(0x{dev.Value:X2})"
        };

        // 充电状态（BYTE2 bit1-0 充电枚举，统一TX V1.3）：0=未充电/1=充电中/2=充满。
        // 工作模式语义已退役（识别=0x13→F13/F14、扩音档=0x16→F15/F16，凭事件另行推导）；
        // 电量 % 充电中灰显不隐藏（数值照出+灰显=虚高仅供参考的提示，见电池管理/Pencil/05）。
        ApplyChargeDisplay(s.ChargeState);
        PencilStateText = dev?.Value == PenDevState.Sleep ? "已连接·休眠" : "已连接";
    }

    /// <summary>充电状态 → 状态卡文字 + 电池环中心徽标（未充电时环内只留 % 数字）。</summary>
    private void ApplyChargeDisplay(byte chargeState)
    {
        PenChargeStateText = chargeState switch
        {
            PenChargeState.None => "未充电",
            PenChargeState.Charging => "充电中",
            PenChargeState.Full => "已充满",
            PenChargeState.Reserved => "保留",
            _ => $"未知({chargeState})"
        };
        (GaugeChargeText, GaugeChargeBrush) = chargeState switch
        {
            PenChargeState.Charging => ("充电中", (System.Windows.Media.Brush)ChargingBrush),
            PenChargeState.Full => ("已充满", (System.Windows.Media.Brush)ChargeFullBrush),
            _ => ("", System.Windows.Media.Brushes.Gray)
        };
        // 灰显语义（指挥官 10-18 定版）：仅充电中灰 %（数值照出=不可靠提示），已充满 FINISH 真判据读数近真不灰
        GaugeValueBrush = chargeState == PenChargeState.Charging ? DimmedValueBrush : NormalValueBrush;
    }

    private void ClearChargeDisplay()
    {
        PenChargeStateText = "—";
        GaugeChargeText = "";
        // 断连即清零（自管口径）：回"待事件"，重连后靠 0x16 补报 + 下一次 K2 边界恢复
        _workStateTracker.Reset();
        WorkStateText = _workStateTracker.DisplayText;
    }

    // ============ K2 启动方式（0x54 信封 0x21/0x42，issues/08 M4 UI）============

    /// <summary>查询 K2 启动方式（`0x42` QUERY_VOICE_KEY_MODE；信封经基站中转当前连接笔端）。</summary>
    [RelayCommand]
    private async Task QueryK2ModeAsync()
    {
        if (!CanUseK2)
        {
            StatusMessage = "请先连接接收器（USB 命令通道），再查询 K2 启动方式（0x54 信封走基站中转）";
            return;
        }

        IsK2Busy = true;
        try
        {
            var r = await _vpCmd.QueryVoiceKeyModeAsync();
            MainThreadHelper.EnsureOnMainThread(() =>
            {
                K2ModeResultText = DescribeK2(r);
                if (r.Success && r.Payload.Length > 0 && r.Payload[0] is 1 or 2)
                    K2ModeIndex = r.Payload[0] - 1;   // 下拉框 0/1 ↔ 方式 1/2
            });
        }
        catch (Exception ex)
        {
            K2ModeResultText = $"查询失败：{ex.Message}";
        }
        finally
        {
            MainThreadHelper.EnsureOnMainThread(() => IsK2Busy = false);
        }
    }

    /// <summary>应用 K2 启动方式（`0x21` SET_VOICE_KEY_MODE；笔端 NVDS 掉电保持）。</summary>
    [RelayCommand]
    private async Task ApplyK2ModeAsync()
    {
        if (!CanUseK2)
        {
            StatusMessage = "请先连接接收器（USB 命令通道），再设置 K2 启动方式（0x54 信封走基站中转）";
            return;
        }

        var mode = K2ModeIndex is 0 or 1 ? K2ModeIndex + 1 : 1;
        IsK2Busy = true;
        try
        {
            var r = await _vpCmd.SetVoiceKeyModeAsync((byte)mode);
            MainThreadHelper.EnsureOnMainThread(() =>
                K2ModeResultText = r.Success ? $"K2 启动方式已设为 方式{mode}（掉电保持）" : DescribeK2(r));
        }
        catch (Exception ex)
        {
            K2ModeResultText = $"设置失败：{ex.Message}";
        }
        finally
        {
            MainThreadHelper.EnsureOnMainThread(() => IsK2Busy = false);
        }
    }

    /// <summary>信封结果 → 用户文案（08 M4 异常映射：02→笔未连接、03→笔端无应答）。</summary>
    private static string DescribeK2(PenCmdResult r) => r.Outcome switch
    {
        PenCmdOutcome.Success when r.Payload.Length > 0 => $"当前 K2 启动方式 = 方式{r.Payload[0]}",
        PenCmdOutcome.Success => "成功",
        PenCmdOutcome.PenNotConnected => "笔未连接（STATUS=02）：笔上电并被基站连接后重试",
        PenCmdOutcome.PenRespTimeout => "笔端无应答（STATUS=03）：等待笔端无线命令面接线后可用",
        PenCmdOutcome.PenRspError => $"笔端拒绝（RSP status=0x{r.PenStatus:X2}）",
        PenCmdOutcome.ParamError => "参数错误（STATUS=01）",
        PenCmdOutcome.Timeout => "无应答（重发耗尽）：基站旧固件不识 0x54 或链路异常",
        _ => r.Message,
    };

    private void StartPolling()
    {
        StopPolling();
        _pollCts = new CancellationTokenSource();
        var token = _pollCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                // 轮询 A1 PAIR_GET_STATE：已连接/配对/模式（Dongle 已实现）——实时反映连接变动
                try { await RefreshPairStateAsync(); }
                catch (Exception ex) { _logger.LogDebug(ex, "A1 poll error"); }

                try { await Task.Delay(2000, token); } catch (OperationCanceledException) { break; }
            }
        }, token);
    }

    private void StopPolling()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
    }

    // ============ Keymap 命令（本地配置 CRUD；设备读写待 0x55，见 pc-issues 0x55 提案） ============

    // ---- 命名配置（pc-issues/04 提案 B；出厂默认只读） ----

    private bool CanMutateKeymapConfig() => !IsFactoryConfigSelected;

    [RelayCommand]
    private void SaveKeymapConfigAs()
    {
        var name = PromptDialog.Show(Application.Current.MainWindow, "另存配置",
            "配置名（保存到 configs\\keymap\\；已预填默认名，可直接回车）：", GenerateCopyName(SelectedKeymapConfig));
        if (string.IsNullOrWhiteSpace(name))
            return;

        try
        {
            _keymapConfigStore.Save(name, KeymapRows.Select(r => r.ToEntry()),
                SwitchRows.Select(r => r.ToEntry()));
            RefreshKeymapConfigNames();
            SelectedKeymapConfig = name;
            IsKeymapDirty = false;
            StatusMessage = $"已另存为配置「{name}」（{KeymapRows.Count} 条）";
        }
        catch (Exception ex)
        {
            StatusMessage = $"另存失败：{ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanMutateKeymapConfig))]
    private void SaveKeymapConfig()
    {
        try
        {
            _keymapConfigStore.Save(SelectedKeymapConfig!, KeymapRows.Select(r => r.ToEntry()),
                SwitchRows.Select(r => r.ToEntry()));
            IsKeymapDirty = false;
            StatusMessage = $"已保存到配置「{SelectedKeymapConfig}」（{KeymapRows.Count} 条）";
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanMutateKeymapConfig))]
    private void RenameKeymapConfig()
    {
        var name = PromptDialog.Show(Application.Current.MainWindow, "重命名配置",
            $"把「{SelectedKeymapConfig}」改名为：", SelectedKeymapConfig);
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, SelectedKeymapConfig, StringComparison.Ordinal))
            return;

        try
        {
            _keymapConfigStore.Rename(SelectedKeymapConfig!, name);
            RefreshKeymapConfigNames();
            SelectedKeymapConfig = name;
            StatusMessage = $"已改名为「{name}」";
        }
        catch (Exception ex)
        {
            StatusMessage = $"改名失败：{ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanMutateKeymapConfig))]
    private void DeleteKeymapConfig()
    {
        var name = SelectedKeymapConfig!;
        if (MessageBox.Show(Application.Current.MainWindow, $"确定删除配置「{name}」？", "删除配置",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            _keymapConfigStore.Delete(name);
            RefreshKeymapConfigNames();
            SelectedKeymapConfig = _keymapConfigStore.GetCurrentName();
            StatusMessage = $"已删除配置「{name}」";
        }
        catch (Exception ex)
        {
            StatusMessage = $"删除失败：{ex.Message}";
        }
    }

    /// <summary>「新建表」：从出厂默认复制一份命名配置——无固件参与，改好触发键即可用键盘自测工作状态自管。</summary>
    [RelayCommand]
    private void NewKeymapConfig()
    {
        var name = PromptDialog.Show(Application.Current.MainWindow, "新建配置", "从出厂默认复制，新配置名（已预填，可直接回车）：",
            GenerateCopyName(_keymapConfigStore.FactoryName));
        if (string.IsNullOrWhiteSpace(name))
            return;
        if (KeymapConfigNames.Contains(name))
        {
            StatusMessage = $"配置「{name}」已存在——换个名字，或选中后「另存为」覆盖";
            return;
        }
        try
        {
            _keymapConfigStore.Save(name,
                KeymapFactoryDefault.Entries.Select(e => e.Clone()),
                KeymapFactoryDefault.SwitchEntries.Select(e => new KeymapSwitchEntry
                { Mode = e.Mode, Modifier = e.Modifier, CodeLo = e.CodeLo }));
            RefreshKeymapConfigNames();
            SelectedKeymapConfig = name;
            StatusMessage = $"已从出厂默认复制新建「{name}」（出厂默认保持只读）";
        }
        catch (Exception ex)
        {
            StatusMessage = $"新建失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 「生效」：`0x55` **整表下发**基站（两段式），**基站回执成功才在上位机生效**（2026-10-18 定版——
    /// 未连接/未响应/中途失败一律保持原状）。选中出厂默认亦可生效=恢复设备默认表。
    /// 整表重发是有意的：设备表现状未知，覆盖式下发免对账。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApplyKeymapConfig))]
    private async Task ApplyKeymapConfigAsync()
    {
        var name = SelectedKeymapConfig!;
        if (IsKeymapDirty)
        {
            StatusMessage = "有未保存的修改——先「保存」再生效";
            return;
        }
        var doc = _keymapConfigStore.Load(name);
        if (doc == null)
        {
            StatusMessage = $"配置不存在或不可读：{name}";
            return;
        }
        if (!_keymapConfig.IsOpen)
        {
            StatusMessage = "未连接基站——生效需先连接接收器（基站回执成功后上位机才同步生效）";
            return;
        }

        var entries = doc.Entries.ToList();
        var switchEntries = (doc.SwitchEntries ?? KeymapFactoryDefault.SwitchEntries.ToList()).ToList();
        StatusMessage = $"正在下发基站（0x55 全表 {entries.Count} 条 + 提交）…";
        try
        {
            var report = await _keymapConfig.ApplyAsync(entries, switchEntries);
            if (report.AllOk)
            {
                _appliedKeymapName = name;
                RebuildWorkStateTracker(name);
                _keymapConfigStore.SetCurrentName(name);
                StatusMessage = $"已下发基站（{report.Written} 条 + NVDS 提交），上位机已同步生效「{name}」";
            }
            else if (!report.ProbeResponded)
                StatusMessage = "基站未响应（0x55 未开通/旧固件）——本次未生效（上位机保持原状）";
            else
                StatusMessage = $"基站下发失败：{report.Error}——本次未生效（上位机保持原状）";
        }
        catch (Exception ex)
        {
            StatusMessage = $"下发异常：{ex.Message}——本次未生效（上位机保持原状）";
        }
    }

    /// <summary>「生效」可用条件：无未保存修改即可（出厂默认也允许=恢复设备默认表；连接性在点击时提示）。</summary>
    private bool CanApplyKeymapConfig() => !IsKeymapDirty;

    // ============ 笔端工作状态自管（产品落地 §三：键盘钩子 + Keymap 联动） ============

    private void OnWorkStateKeyDown(int vk)
    {
        var tracker = _workStateTracker;
        if (!tracker.Feed(KeyboardUsageMap.FromVirtualKey(vk)))
            return;
        MainThreadHelper.EnsureOnMainThread(() => WorkStateText = tracker.DisplayText);
    }

    /// <summary>重建触发键绑定（只在「生效」回执成功后调用——绑定跟随已应用配置，不随选择切换）。</summary>
    private void RebuildWorkStateTracker(string? name)
    {
        _workStateTracker = BuildWorkStateTracker(name);
        WorkStateText = _workStateTracker.DisplayText;
    }

    private PenWorkStateTracker BuildWorkStateTracker(string? name)
    {
        try
        {
            name ??= _keymapConfigStore.GetCurrentName();
            var doc = _keymapConfigStore.Load(name);
            var binding = PenWorkStateBinding.FromKeymap(
                doc?.Entries ?? [],
                doc?.SwitchEntries ?? KeymapFactoryDefault.SwitchEntries.ToList(),
                name);
            return new PenWorkStateTracker(binding);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "工作状态触发键推导失败，回落出厂默认绑定");
            return new PenWorkStateTracker(PenWorkStateBinding.FromKeymap(
                KeymapFactoryDefault.Entries, KeymapFactoryDefault.SwitchEntries, _keymapConfigStore.FactoryName));
        }
    }

    /// <summary>另存/新建的预填名：出厂默认→「出厂默认副本」，命名配置→「&lt;名&gt;_副本」；重名自动加序号，回车即纳。</summary>
    private string GenerateCopyName(string? sourceName)
    {
        var baseName = string.IsNullOrEmpty(sourceName) ||
                       string.Equals(sourceName, _keymapConfigStore.FactoryName, StringComparison.Ordinal)
            ? "出厂默认副本"
            : $"{sourceName}_副本";
        var name = baseName;
        for (var i = 2; _keymapConfigStore.ListNames().Contains(name); i++)
            name = $"{baseName}{i}";
        return name;
    }

    /// <summary>刷新配置下拉；保持原选中，失效则回落当前记录（或出厂默认）。</summary>
    private void RefreshKeymapConfigNames()
    {
        var current = SelectedKeymapConfig;
        KeymapConfigNames = new ObservableCollection<string>(_keymapConfigStore.ListNames());
        SelectedKeymapConfig = current != null && KeymapConfigNames.Contains(current)
            ? current
            : _keymapConfigStore.GetCurrentName();
    }

    /// <summary>
    /// 把当前选中配置载入一键一卡视图（出厂默认带"只读"标注）。
    /// 行实例在 <see cref="KeymapRows"/>（保存真值序）与 <see cref="KeymapCards"/>（按键分组视图）间共享，
    /// 卡片上的编辑即时反映到保存路径（Save 用 KeymapRows）。
    /// </summary>
    private void LoadSelectedConfigIntoCards()
    {
        var name = SelectedKeymapConfig;
        var doc = string.IsNullOrEmpty(name) ? null : _keymapConfigStore.Load(name);
        var entries = doc?.Entries ?? [];
        var count = entries.Count;
        var isEditable = doc != null && !IsFactoryConfigSelected;

        MainThreadHelper.EnsureOnMainThread(() =>
        {
            var rows = entries.Select((e, i) => new KeymapRowViewModel((byte)i, e, isEditable)).ToList();
            foreach (var row in rows)
                row.PropertyChanged += OnKeymapRowPropertyChanged;

            // S1 切换表：配置未携带（旧文件/迁移文件）→ 回落出厂真值（2026-10-17 起随配置携带）
            var switchEntries = doc?.SwitchEntries ?? KeymapFactoryDefault.SwitchEntries.ToList();
            var switchRows = switchEntries
                .Select(e => new KeymapSwitchRowViewModel(e, isEditable))
                .OrderBy(r => r.Mode)
                .ToList();
            foreach (var row in switchRows)
                row.PropertyChanged += OnKeymapRowPropertyChanged;

            KeymapRows = new ObservableCollection<KeymapRowViewModel>(rows);
            SwitchRows = new ObservableCollection<KeymapSwitchRowViewModel>(switchRows);
            RebuildCards();

            if (doc == null)
                StatusMessage = string.IsNullOrEmpty(name) ? "未选择配置" : $"配置「{name}」不可读";
            else if (IsFactoryConfigSelected)
                StatusMessage = $"出厂默认（{count} 条，只读——字节级照抄固件 s_keymap_default）";
            else
                StatusMessage = $"已加载配置「{name}」（{count} 条）";
        });
    }

    /// <summary>行级任意编辑 → 标脏（捕获框/下拉/复选/事件与动作切换都经行属性变更）。</summary>
    private void OnKeymapRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => IsKeymapDirty = true;

    /// <summary>按物理键重建卡片视图（行实例不变，仅重组）。</summary>
    private void RebuildCards()
    {
        KeymapCards = new ObservableCollection<KeymapCardViewModel>(
            KeymapRows.GroupBy(r => r.KeyId)
                .Select(g => new KeymapCardViewModel(g.Key, g.First().IsEditable, g))
                .OrderBy(c => c.KeyId));
    }

    /// <summary>给按键卡追加一行事件（出厂默认拒绝；事件列守卫与 CLI keymap set 同规则）。</summary>
    [RelayCommand]
    private void AddKeymapRow(KeymapCardViewModel? card)
    {
        if (card == null || !CanMutateKeymapConfig())
            return;

        var nextEvent = PickNextEvent(card.KeyId, card.Rows.Select(r => r.EventType));
        if (nextEvent == null)
        {
            StatusMessage = $"{card.KeyName} 的可用事件已全部在列（K2 事件为语音会话边界契约，不可扩展）";
            return;
        }

        var row = new KeymapRowViewModel(0, new KeymapEntry { KeyId = card.KeyId, EventType = nextEvent.Value }, true);
        row.PropertyChanged += OnKeymapRowPropertyChanged;

        var rows = KeymapRows.ToList();
        var idx = rows.FindIndex(r => r.KeyId > row.KeyId ||
            (r.KeyId == row.KeyId && (byte)r.EventType > (byte)row.EventType));
        if (idx < 0) rows.Add(row); else rows.Insert(idx, row);
        RenumberRows(rows);

        KeymapRows = new ObservableCollection<KeymapRowViewModel>(rows);
        RebuildCards();
        IsKeymapDirty = true;
        StatusMessage = $"已添加 {card.KeyName} {KeymapFormatter.GetEventTypeName(nextEvent.Value)}（空动作，请设置键值后保存）";
    }

    /// <summary>删除一行（出厂默认拒绝；K2 行是语音会话边界契约，不可删）。</summary>
    [RelayCommand]
    private void RemoveKeymapRow(KeymapRowViewModel? row)
    {
        if (row == null || !CanMutateKeymapConfig() || KeymapRules.IsK2(row.KeyId))
            return;

        var rows = KeymapRows.ToList();
        rows.Remove(row);
        RenumberRows(rows);

        KeymapRows = new ObservableCollection<KeymapRowViewModel>(rows);
        RebuildCards();
        IsKeymapDirty = true;
    }

    private static void RenumberRows(List<KeymapRowViewModel> rows)
    {
        for (var i = 0; i < rows.Count; i++)
            rows[i].Index = (byte)i;
    }

    /// <summary>按键选下一个可追加的事件（K2 只允许会话边界两事件）。</summary>
    private static KeyEventType? PickNextEvent(byte keyId, IEnumerable<KeyEventType> present)
    {
        var candidates = KeymapRules.IsK2(keyId)
            ? new[] { KeyEventType.LongPress, KeyEventType.LongRelease }
            : new[] { KeyEventType.Click, KeyEventType.DoubleClick, KeyEventType.LongPress, KeyEventType.LongRelease };
        return candidates.FirstOrDefault(ev => !present.Contains(ev));
    }

    // ============ 事件处理 ============

    private void OnConnectionStateChanged(object? sender, bool connected)
    {
        MainThreadHelper.EnsureOnMainThread(() =>
        {
            IsConnected = connected;
            if (!connected)
            {
                StopPenPushLoop(null);
                PencilConnected = false;
                BatteryLevel = 0;
                Rssi = 0;
                HasPenTelemetry = false;
                PencilStateText = "未连接";
                PenDevStateText = "—";
                ClearChargeDisplay();
                PenStatusMetaText = "未获取（连接后点「刷新笔端状态」）";
                K2ModeResultText = "未查询：应用/查询经 0x54 信封 → 基站 → 当前连接笔端";
                DonglePairText = "未连接：配对状态不可用";
                HasPairState = false;
            }
        });
    }

    private void OnDeviceListChanged(object? sender, IReadOnlyList<HidDeviceInfo> devices)
    {
        MainThreadHelper.EnsureOnMainThread(() =>
        {
            AvailableDevices = new ObservableCollection<HidDeviceInfo>(devices);
        });
    }

    private void OnDeviceInfoChanged(object? sender, DeviceInfo? info)
    {
        MainThreadHelper.EnsureOnMainThread(() =>
        {
            DongleInfo = info;
        });
    }

    private void OnPencilStateUpdated(object? sender, PencilState state)
    {
        MainThreadHelper.EnsureOnMainThread(() =>
        {
            if (state.Status != 0x00)
            {
                HasPenTelemetry = false;
                return;   // 0x42 未实现/失败：不覆盖卡片
            }
            HasPenTelemetry = true;
            BatteryLevel = state.Battery == 0xFF ? 0 : state.Battery;
            PencilConnected = state.IsConnected;
            Rssi = state.Rssi;
            // 0x42 mode 字段=byte2 充电枚举副本（V1.3，与 0x53 同源）
            ApplyChargeDisplay((byte)state.ChargeState);
        });
    }
}
