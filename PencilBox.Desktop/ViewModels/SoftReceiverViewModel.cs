// <copyright file="SoftReceiverViewModel.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using PencilBox.Core.Ble;
using PencilBox.Core.Ble.SoftReceiver;
using PencilBox.Core.Communication;
using PencilBox.Core.Models;
using PencilBox.Core.Protocol.Company;
using PencilBox.Core.Services;
using PencilBox.Desktop.Helpers;
using PencilBox.Infrastructure.Audio;
using PencilBox.Infrastructure.Hid;
using PencilBox.Infrastructure.Platform;

namespace PencilBox.Desktop.ViewModels;

/// <summary>
/// 软件接收器页（形态③：PC 直连键盘收语音，绕过硬件基站）。
/// 状态卡（连接/电量/充电/档位）+ 事件流（键/档位/槽/seq 跳）+ 扩音泵出开关。
/// 会话编排/解析/解码在 Core（<see cref="SoftReceiverSession"/>），本 VM 只做编排与展示；
/// 扩音泵复用 <see cref="IAudioPassthrough"/> 外源模式（ExternalSource，跳过 WASAPI 采集）。
/// 机制文档 = _share/box/docs/GitFlow/feature/soft-receiver/README.md。
/// </summary>
public partial class SoftReceiverViewModel : ObservableObject
{
    private const int EventLogCapacity = 200;

    private readonly IBleCentral _central;
    private readonly IPenBindingService _penBinding;
    private readonly IAudioPassthrough _passthrough;
    private readonly IHidTransport _hidTransport;

    private SoftReceiverSession? _session;
    private SrxHidSession? _hidSession;
    private readonly Stopwatch _speakerClock = new();
    private DispatcherTimer? _speakerStatsTimer;
    private SrxKeyActionExecutor? _keyActions;
    /// <summary>最近一次 S1 档位（0xFF=尚未见过；开软件键鼠时预置基线防假切换）</summary>
    private byte _lastSwitchMode = 0xFF;

    public SoftReceiverViewModel(IBleCentral central, IPenBindingService penBinding, IAudioPassthrough passthrough,
        IHidTransport hidTransport)
    {
        _central = central;
        _penBinding = penBinding;
        _passthrough = passthrough;
        _hidTransport = hidTransport;
        _ = LoadKnownPensAsync();   // 启动即载历史目录（仪表盘同款：BLE 扫描+串口 0x51 两源一份存储）
        _ = RefreshAudioTargetsAsync();   // 启动刷新输出/桥目标候选（PCM 路由可选化 8i）
    }

    // ---- PCM 路由可选化（8i）：解码 PCM 去向=扬声器输出可选、桥目标可选，不写死 ----
    private const string AutoSpeakerOutput = "（系统默认扬声器）";
    private const string AutoBridgeTarget = "（自动配对）";
    private List<AudioDeviceInfo> _renderInfos = [];
    private List<string> _bridgeRenderNames = [];

    /// <summary>扩音输出候选（Auto=系统默认引擎自选；其余=GetRenderDevicesAsync 实枚举）</summary>
    public ObservableCollection<string> SpeakerOutputOptions { get; } = [AutoSpeakerOutput];
    [ObservableProperty] private string _selectedSpeakerOutput = AutoSpeakerOutput;

    /// <summary>桥目标候选（Auto=SelectPair 自动配对；其余=活动播放设备，开桥时找同族录音口）</summary>
    public ObservableCollection<string> BridgeTargetOptions { get; } = [AutoBridgeTarget];
    [ObservableProperty] private string _selectedBridgeTarget = AutoBridgeTarget;

    /// <summary>刷新输出/桥目标下拉（启动+连接成功后调用；保住原选中）。</summary>
    private async Task RefreshAudioTargetsAsync()
    {
        try
        {
            var infos = await _passthrough.GetRenderDevicesAsync();
            _renderInfos = infos.ToList();
            var keepS = SelectedSpeakerOutput;
            SpeakerOutputOptions.Clear();
            SpeakerOutputOptions.Add(AutoSpeakerOutput);
            foreach (var n in _renderInfos.Select(d => d.FriendlyName))
                SpeakerOutputOptions.Add(n);
            SelectedSpeakerOutput = SpeakerOutputOptions.FirstOrDefault(o => o == keepS) ?? AutoSpeakerOutput;

            using var enumerator = new MMDeviceEnumerator();
            _bridgeRenderNames = enumerator
                .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Select(d => d.FriendlyName).ToList();
            var keepB = SelectedBridgeTarget;
            BridgeTargetOptions.Clear();
            BridgeTargetOptions.Add(AutoBridgeTarget);
            foreach (var n in _bridgeRenderNames)
                BridgeTargetOptions.Add(n);
            SelectedBridgeTarget = BridgeTargetOptions.FirstOrDefault(o => o == keepB) ?? AutoBridgeTarget;
        }
        catch (Exception ex)
        {
            Log($"音频设备列表刷新失败：{ex.Message}");
        }
    }

    /// <summary>扩音输出 → 引擎 RenderDeviceId（Auto=null=系统默认）；**延迟实测 chirp 发射端同用此选择**
    /// （2026-11-05 指挥官：默认扬声器不可见致"实测没声音"，两功能同打一台外放=语义统一）。</summary>
    private string? ResolveSpeakerRenderId()
    {
        if (SelectedSpeakerOutput is null || SelectedSpeakerOutput == AutoSpeakerOutput)
            return null;
        return _renderInfos.FirstOrDefault(d => d.FriendlyName == SelectedSpeakerOutput)?.DeviceId;
    }

    // ---- 软件增益（11-03 指挥官需求：原始人声过轻，系统音量拉满仍听不清）----
    /// <summary>增益滑条 0~100（对数刻度，同扩音页口径）：实际增益 = MaxVolume^(位置/100)；默认 50≈4.9x。</summary>
    [ObservableProperty]
    private double _gainSlider = 50f;

    /// <summary>实际软件增益 1x~24x；&gt;1x 由 SoftLimiter 天花板防破音。</summary>
    public double SoftwareGain => Math.Pow(PassthroughStartOptions.MaxVolume, GainSlider / 100.0);

    public string GainText => $"{SoftwareGain:F1}x";

    partial void OnGainSliderChanged(double value)
    {
        OnPropertyChanged(nameof(SoftwareGain));
        OnPropertyChanged(nameof(GainText));
        if (IsSpeakerOn)
        {
            if (_exclusiveEngine != null)
                _exclusiveEngine.SetVolume((float)SoftwareGain);   // 独占引擎即调即生效
            else
                _passthrough.SetVolume((float)SoftwareGain);
        }
    }

    // ---- 数据源（08 号方案：BLE 直连 | USB EP4 HID 直送；SelectedIndex 绑定避开枚举转换器坑） ----
    /// <summary>0=BLE 直连（默认，现状流程）；1=USB EP4 HID 直送（基站 mSBC 64B 定长报告直达 PC）。</summary>
    public string[] SourceOptions { get; } = ["BLE 直连", "USB EP4 HID 直送"];

    [NotifyPropertyChangedFor(nameof(IsHidSource))]
    [NotifyPropertyChangedFor(nameof(CanScan))]     // 换源门控三处 Can* 同步刷新（坑册：多标志门控必反查）
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    [NotifyPropertyChangedFor(nameof(CanProbe))]    // 延迟实测双腿（2026-11-05）：换源即换可用域
    [NotifyPropertyChangedFor(nameof(ProbeHint))]
    [ObservableProperty] private int _selectedSourceIndex;

    /// <summary>当前是否处于 EP4 HID 直送数据源（决定键盘选择区让位/键鼠电量面板置灰）</summary>
    public bool IsHidSource => SelectedSourceIndex == 1;

    /// <summary>换源自动切换编排中（断腿→拨回目标源期间吞掉中间抖动）。</summary>
    private bool _sourceSwitching;

    partial void OnSelectedSourceIndexChanged(int value)
    {
        // 互斥守卫升级（2026-11-05 指挥官：连接成功后切不回去不方便）——
        // 有腿在连=**自动断开完成切换**（原设计只弹提示回拨=用户必须手动先断再切）；
        // 实测/连接操作中换源仍拒绝（断会话毁实测/竞态）。双会话抢事件的防护不断：腿断干净才切。
        if (_sourceSwitching)
            return;   // 自动切换编排中，忽略中间抖动
        var busy = IsProbing || IsBusy || IsHidBusy;
        if (value == 1 && (IsConnected || Session != null))
        {
            if (busy)
            {
                Log("实测/连接操作进行中，等它结束再换数据源");
                SelectedSourceIndex = 0;
                return;
            }
            Log("BLE 腿在连：自动断开后切到 USB EP4…");
            _sourceSwitching = true;
            SelectedSourceIndex = 0;
            _ = SwitchSourceAfterDisconnectAsync(target: 1, hidLeg: false);
            return;
        }
        if (value == 0 && IsHidConnected)
        {
            if (busy)
            {
                Log("实测/连接操作进行中，等它结束再换数据源");
                SelectedSourceIndex = 1;
                return;
            }
            Log("EP4 腿在连：自动断开后切到 BLE…");
            _sourceSwitching = true;
            SelectedSourceIndex = 1;
            _ = SwitchSourceAfterDisconnectAsync(target: 0, hidLeg: true);
            return;
        }
        if (value == 1)
        {
            Log("数据源 → USB EP4 HID 直送（基站须烧 test/hid 固件；键动作/鼠标/电量此路不经基站转发，面板置灰）");
            _ = RefreshHidDevicesAsync();
        }
        else
        {
            Log("数据源 → BLE 直连");
        }
    }

    /// <summary>换源自动断开：断腿（清场含 route off/泵出/桥/录制）完成后把下拉拨到目标源——
    /// 再触发的 OnSelectedSourceIndexChanged 里腿已断，自然走正常切换分支。断失败则提示手动处理。</summary>
    private async Task SwitchSourceAfterDisconnectAsync(int target, bool hidLeg)
    {
        try
        {
            if (hidLeg) await HidDisconnectAsync();
            else await DisconnectAsync();
        }
        catch (Exception ex)
        {
            Log($"自动断开失败：{ex.Message}——请手动断开后再换数据源");
        }
        MainThreadHelper.EnsureOnMainThread(() =>
        {
            SelectedSourceIndex = target;   // 与当前值相同则不触发变更，仅清编排旗标
            _sourceSwitching = false;
        });
    }

    // ---- USB EP4 HID 直送（基站侧 mSBC 直传，08 号方案 M1） ----
    /// <summary>EP4 下拉选项（枚举过滤 IsCommandChannel；显示=名称+路径尾巴，防多台混选）。</summary>
    public sealed record HidDeviceOption(HidDeviceInfo Device, string Display);

    public ObservableCollection<HidDeviceOption> HidDevices { get; } = [];
    [NotifyPropertyChangedFor(nameof(CanHidConnect))]   // 选中台变化→连接按钮亮灭（坑册 10-19 读Hash永灰同族：门控漏挂）
    [ObservableProperty] private HidDeviceOption? _selectedHidDevice;
    [NotifyPropertyChangedFor(nameof(CanRefreshHidDevices))]
    [NotifyPropertyChangedFor(nameof(CanHidConnect))]
    [NotifyPropertyChangedFor(nameof(CanHidDisconnect))]
    [NotifyPropertyChangedFor(nameof(CanToggleSpeaker))]    // HID 连接态进泵出/桥可用域（M2：泵出腿）
    [NotifyPropertyChangedFor(nameof(CanToggleBridge))]
    [NotifyPropertyChangedFor(nameof(CanToggleKeyActions))]
    [NotifyPropertyChangedFor(nameof(CanRecord))]
    [NotifyPropertyChangedFor(nameof(CanProbe))]    // 延迟实测 HID 腿（2026-11-05）：连接态进可用域
    [NotifyPropertyChangedFor(nameof(ProbeHint))]
    [ObservableProperty] private bool _isHidConnected;
    [NotifyPropertyChangedFor(nameof(CanRouteHidOn))]
    [NotifyPropertyChangedFor(nameof(CanRouteHidOff))]
    [NotifyPropertyChangedFor(nameof(CanProbe))]    // 路由未切入时 EP4 通道无音频帧，实测腿必须门死
    [NotifyPropertyChangedFor(nameof(ProbeHint))]
    [ObservableProperty] private bool _isHidRouted;
    [ObservableProperty] private string _hidStatusText = "UAC（未切入）";
    [NotifyPropertyChangedFor(nameof(CanRefreshHidDevices))]
    [NotifyPropertyChangedFor(nameof(CanHidConnect))]
    [NotifyPropertyChangedFor(nameof(CanHidDisconnect))]
    [NotifyPropertyChangedFor(nameof(CanRouteHidOn))]
    [NotifyPropertyChangedFor(nameof(CanRouteHidOff))]
    [ObservableProperty] private bool _isHidBusy;
    private long _hidFrameCount;

    public bool CanRefreshHidDevices => IsHidSource && !IsHidBusy;
    public bool CanHidConnect => IsHidSource && !IsHidBusy && !IsHidConnected && SelectedHidDevice != null;
    public bool CanHidDisconnect => IsHidSource && !IsHidBusy && IsHidConnected;
    public bool CanRouteHidOn => IsHidConnected && !IsHidRouted && !IsHidBusy;
    public bool CanRouteHidOff => IsHidConnected && IsHidRouted && !IsHidBusy;

    [RelayCommand]
    private async Task RefreshHidDevicesAsync()
    {
        if (!CanRefreshHidDevices) return;
        IsHidBusy = true;
        try
        {
            var devices = await _hidTransport.EnumerateDevicesAsync();
            var ep4s = devices.Where(d => d.IsCommandChannel).ToList();
            var keep = SelectedHidDevice?.Device.DevicePath;
            HidDevices.Clear();
            foreach (var d in ep4s)
            {
                var tail = d.DevicePath.Replace("\\??\\", "");
                var hash = tail.IndexOf('#');
                var pathTail = hash >= 0 ? tail[(hash + 1)..] : tail;
                var end = pathTail.IndexOf("#{");
                HidDevices.Add(new HidDeviceOption(d, $"{d.ProductName}  {(end >= 0 ? pathTail[..end] : pathTail)}"));
            }
            SelectedHidDevice = HidDevices.FirstOrDefault(h =>
                string.Equals(h.Device.DevicePath, keep, StringComparison.OrdinalIgnoreCase))
                ?? HidDevices.FirstOrDefault();
            Log(HidDevices.Count > 0
                ? $"EP4 枚举：基站命令通道 {HidDevices.Count} 台（0xFF02 无 RID）"
                : "EP4 枚举：未发现基站命令通道（基站未插/驱动未拉起？仪表盘 device 列表看全表）");
        }
        catch (Exception ex)
        {
            Log($"EP4 枚举失败：{ex.Message}");
        }
        finally
        {
            IsHidBusy = false;
        }
    }

    [RelayCommand]
    private async Task HidConnectAsync()
    {
        if (!CanHidConnect || SelectedHidDevice == null) return;
        var opt = SelectedHidDevice;
        IsHidBusy = true;
        try
        {
            if (!await _hidTransport.ConnectAsync(opt.Device.DevicePath))
            {
                Log($"EP4 连接失败：{opt.Device.ProductName}");
                return;
            }
            if (!_hidTransport.IsNoReportIdDevice)
            {
                Log("EP4 连接的不是 0xFF02 无 RID 承载（识别异常），已断开");
                await _hidTransport.DisconnectAsync();
                return;
            }
            _hidSession = new SrxHidSession(_hidTransport, msg => Log(msg));
            _hidSession.AudioFrameReady += OnHidAudioFrame;
            _hidSession.WatchdogFallback += OnHidWatchdog;
            _hidFrameCount = 0;
            IsHidConnected = true;
            IsHidRouted = false;
            HidStatusText = "UAC（未切入）";
            EnsureStatsTimer();
            Log($"EP4 已连接：{opt.Device.ProductName}（读线程 Highest 常驻；「切入 HID」后音频报告开始到达）");
        }
        catch (Exception ex)
        {
            Log($"EP4 连接异常：{ex.Message}");
            await CleanupHidAsync();
        }
        finally
        {
            IsHidBusy = false;
        }
    }

    /// <summary>切入 HID 直送（0x56 route=1）：OK 应答经 ReadFile 到达=读线程消费活证明（时序门自然满足）。</summary>
    [RelayCommand]
    private async Task RouteHidOnAsync()
    {
        if (!CanRouteHidOn || _hidSession == null) return;
        IsHidBusy = true;
        try
        {
            var ok = await _hidSession.RouteToHidAsync();
            IsHidRouted = ok && _hidSession.IsRoutedHid;
            HidStatusText = IsHidRouted ? "HID 直送中" : "UAC（切入失败）";
            Log(IsHidRouted
                ? "已切入 HID 直送（mSBC 60B 经 EP4 64B 定长报告直达，u16 帧计数缺口自动补静音）"
                : "切入失败（见事件流应答原因）——UAC 路不受影响");
        }
        catch (Exception ex)
        {
            Log($"切入异常：{ex.Message}");
        }
        finally
        {
            IsHidBusy = false;
        }
    }

    /// <summary>切回 UAC 默认路（0x56 route=0）；幂等安全。</summary>
    [RelayCommand]
    private async Task RouteHidOffAsync()
    {
        if (!CanRouteHidOff || _hidSession == null) return;
        IsHidBusy = true;
        try
        {
            var ok = await _hidSession.RouteToUacAsync();
            IsHidRouted = ok && _hidSession.IsRoutedHid;
            HidStatusText = "UAC（已切回）";
            Log(ok ? "已切回 UAC 默认路（免驱常态恢复）" : "切回无应答/拒绝——基站可能已自行回退或仍直送（拔插恢复）");
        }
        catch (Exception ex)
        {
            Log($"切回异常：{ex.Message}");
        }
        finally
        {
            IsHidBusy = false;
        }
    }

    [RelayCommand]
    private async Task HidDisconnectAsync()
    {
        if (!CanHidDisconnect) return;
        IsHidBusy = true;
        try
        {
            await CleanupHidAsync();
            Log("EP4 已断开");
        }
        finally
        {
            IsHidBusy = false;
        }
    }

    /// <summary>HID 腿清场：路由着的先尽力切回 UAC（保免驱常态），再拆会话+断传输（传输层为共享单例，勿 Dispose）。</summary>
    private async Task CleanupHidAsync()
    {
        var session = _hidSession;
        _hidSession = null;
        if (session != null)
        {
            if (session.IsRoutedHid)
            {
                try
                {
                    if (await session.RouteToUacAsync())
                        Log("已切回 UAC 默认路");
                    else
                        Log("⚠ 切回 UAC 无应答——基站若仍直送，拔插恢复；OTA 会话启动会自动 route=0 兜底");
                }
                catch (Exception ex)
                {
                    Log($"切回 UAC 异常：{ex.Message}");
                }
            }
            session.AudioFrameReady -= OnHidAudioFrame;
            session.WatchdogFallback -= OnHidWatchdog;
            session.Dispose();
        }
        try { await _hidTransport.DisconnectAsync(); } catch { /* 已断开容错 */ }
        IsHidConnected = false;
        IsHidRouted = false;
        HidStatusText = "UAC（未切入）";
        if (IsSpeakerOn)
            ToggleSpeakerExecute();   // 与 BLE 断开同款清场：泵出/独占随断开自动关
        if (IsRecording)
            StopRecord();
        MaybeStopStatsTimer();
    }

    private void OnHidWatchdog(object? sender, EventArgs e)
    {
        MainThreadHelper.EnsureOnMainThread(() =>
        {
            IsHidRouted = false;
            HidStatusText = "看门狗回退→UAC";
        });
    }

    /// <summary>HID 解码帧统一消费（与 BLE 腿同漏斗）：扩音泵/桥/录制并列，互不互斥。</summary>
    private void OnHidAudioFrame(object? sender, SrxHidAudioFrame frame)
    {
        Interlocked.Increment(ref _hidFrameCount);
        if (IsSpeakerOn)
        {
            if (_exclusiveEngine != null)
                _exclusiveEngine.WritePcm(frame.Pcm);
            else
                _ = _passthrough.WriteExternalPcm(frame.Pcm);
        }
        _bridge?.WritePcm(frame.Pcm);
        lock (_recordLock)
        {
            if (_recordWriter != null)
            {
                _recordWriter.Write(frame.Pcm);
                _recordFrames++;
            }
        }
    }

    // ---- 连接状态卡 ----
    [NotifyPropertyChangedFor(nameof(CanProbe))]
    [NotifyPropertyChangedFor(nameof(CanToggleSpeaker))]
    [NotifyPropertyChangedFor(nameof(CanToggleBridge))]
    [NotifyPropertyChangedFor(nameof(CanToggleKeyActions))]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    [NotifyPropertyChangedFor(nameof(CanScan))]
    [ObservableProperty] private bool _isBusy;
    [NotifyPropertyChangedFor(nameof(CanProbe))]
    [NotifyPropertyChangedFor(nameof(CanToggleSpeaker))]
    [NotifyPropertyChangedFor(nameof(CanToggleBridge))]
    [NotifyPropertyChangedFor(nameof(CanToggleKeyActions))]
    [NotifyPropertyChangedFor(nameof(CanRecord))]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    [NotifyPropertyChangedFor(nameof(ProbeHint))]
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _connectionText = "未连接";

    // ---- 键盘选择（下拉=仪表盘同款已知笔端目录，两源一份存储；扫描/连接分开） ----
    /// <summary>下拉选项（每次扫描/载入整体重建；排序=目录三级层级：上次使用＞使用过＞出现过）</summary>
    public ObservableCollection<KnownPen> PenOptions { get; } = [];
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    [ObservableProperty] private KnownPen? _selectedPen;
    [NotifyPropertyChangedFor(nameof(CanScan))]
    [ObservableProperty] private bool _isScanningDevices;

    /// <summary>「扫描」可用 = BLE 源、不忙且没在扫（已连接也允许扫，顺带刷新在场/RSSI）</summary>
    public bool CanScan => !IsHidSource && !IsBusy && !IsScanningDevices;

    /// <summary>「连接」可用 = BLE 源、未连接、不忙、且下拉已选笔端</summary>
    public bool CanConnect => !IsHidSource && !IsConnected && !IsBusy && SelectedPen != null;
    [ObservableProperty] private string _batteryText = "—";
    [ObservableProperty] private string _chargeText = "—";
    [ObservableProperty] private string _devStateText = "—";
    [ObservableProperty] private string _modeText = "—";
    [ObservableProperty] private string _statsText = "—";
    [NotifyPropertyChangedFor(nameof(CanToggleSpeaker))]
    [NotifyPropertyChangedFor(nameof(SpeakerButtonText))]
    [NotifyPropertyChangedFor(nameof(CanChangeExclusiveRender))]
    [ObservableProperty] private bool _isSpeakerOn;
    [ObservableProperty] private string _speakerText = "关";

    // ---- 独占渲染（08 号 M3：WASAPI Exclusive 吃满引擎税；无 AEC/ANS=纯渲染 A/B 腿） ----
    private ExclusiveRenderEngine? _exclusiveEngine;

    /// <summary>独占渲染开关（只在扩音关着时可改——运行中改须先关再开，防半拆链状态）。</summary>
    public bool CanChangeExclusiveRender => !IsSpeakerOn;

    [ObservableProperty] private bool _isExclusiveRender;

    // ---- 虚拟麦克风桥（R1PBleAudio 桥式腿，复用 SrxBridgePump） ----
    [NotifyPropertyChangedFor(nameof(CanToggleBridge))]
    [ObservableProperty] private bool _isBridgeOn;
    [ObservableProperty] private string _bridgeText = "关";
    [ObservableProperty] private string _bridgeDeviceText = "—";
    [ObservableProperty] private string _bridgeInfoText = "—";

    // ---- 收流录制 WAV（receive 腿） ----
    [NotifyPropertyChangedFor(nameof(CanRecord))]
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private string _recordText = "录制 WAV";

    /// <summary>「录制 WAV」按钮可用 = BLE 连接或 EP4 直送连接（两路漏斗同款 _recordWriter）</summary>
    public bool CanRecord => IsConnected || IsHidConnected;

    // ---- 软件键鼠（键盘腿=Keymap 表驱动注入；鼠标腿=空鼠槽直通；默认关） ----
    [NotifyPropertyChangedFor(nameof(CanToggleKeyActions))]
    [NotifyPropertyChangedFor(nameof(KeyActionsButtonText))]
    [ObservableProperty] private bool _isKeyActionsOn;
    [ObservableProperty] private string _keyActionsText = "关";

    // ---- 延迟实测（chirp 回环，复用 SoftReceiverProbe） ----
    [NotifyPropertyChangedFor(nameof(CanProbe))]
    [NotifyPropertyChangedFor(nameof(CanToggleSpeaker))]
    [NotifyPropertyChangedFor(nameof(CanToggleBridge))]
    [NotifyPropertyChangedFor(nameof(CanToggleKeyActions))]
    [ObservableProperty] private bool _isProbing;
    [ObservableProperty] private string _probeSecondsInput = "30";
    [ObservableProperty] private string _probeResultText = "（未测）";

    private CancellationTokenSource? _probeCts;
    private SrxBridgePump? _bridge;
    private WaveFileWriter? _recordWriter;
    private readonly object _recordLock = new();
    private long _recordFrames;
    private Stopwatch? _recordSw;

    /// <summary>HID 直送腿实测可用 = EP4 已连且路由已切入——路由不在 HID 时音频走 UAC，
    /// EP4 通道收不到任何帧，门死（2026-11-05 双腿化）。</summary>
    public bool CanProbeHid => IsHidConnected && IsHidRouted;

    /// <summary>「开始延迟实测」可用 = 任一腿就绪（BLE 已连 / EP4 已连+已路由）且未在测未忙
    /// （按钮 IsEnabled 直绑，勿用转换器）</summary>
    public bool CanProbe => (IsConnected || CanProbeHid) && !IsProbing && !IsBusy;

    /// <summary>实测按钮旁常显提示：腿未就绪时说明为什么灰——
    /// tooltip 在禁用态不弹出（WPF 禁用控件不参与命中测试），灰了必须让用户当场看见原因。</summary>
    public string ProbeHint => IsConnected || CanProbeHid
        ? ""
        : IsHidSource
            ? "灰置原因：EP4 未连接或未切入 HID 直送（先「连接 EP4」再「切入 HID 直送」，chirp 回传须走 EP4 通道）"
            : "灰置原因：未连接笔端（BLE 腿需「扫描 → 选笔端 → 连接」）";

    /// <summary>「扩音泵出」按钮可用 = 已连接（BLE 或 EP4 直送），或扩音正开着（允许随时关）；实测中锁死</summary>
    public bool CanToggleSpeaker => (IsConnected || IsHidConnected || IsSpeakerOn) && !IsProbing;

    /// <summary>扩音按钮文案随态翻转</summary>
    public string SpeakerButtonText => IsSpeakerOn ? "关扩音" : "开扩音";

    /// <summary>「虚拟麦克风桥」按钮可用 = 已连接（BLE 或 EP4 直送），或桥正开着（允许随时关）；实测中锁死</summary>
    public bool CanToggleBridge => (IsConnected || IsHidConnected || IsBridgeOn) && !IsProbing && !IsBusy;

    /// <summary>「软件键鼠」按钮可用 = BLE 源已连接（0x13 语义键/空鼠槽不经基站 EP4 转发，HID 路不可得），或正开着；实测/忙时锁死</summary>
    public bool CanToggleKeyActions => !IsHidSource && (IsConnected || IsKeyActionsOn) && !IsProbing && !IsBusy;

    /// <summary>软件键鼠按钮文案随态翻转</summary>
    public string KeyActionsButtonText => IsKeyActionsOn ? "关软件键鼠" : "开软件键鼠";

    /// <summary>事件流（最新在下；容量裁剪防长会话膨胀）</summary>
    public ObservableCollection<string> EventLog { get; } = [];

    private SoftReceiverSession? Session
    {
        get => _session;
        set
        {
            if (_session != null)
            {
                _session.AudioFrameReady -= OnAudioFrameReady;
                _session.KeyEvent -= OnKeyEvent;
                _session.KeyAction -= OnKeyAction;
                _session.MouseButton -= OnMouseButton;
                _session.MouseFrame -= OnMouseFrame;
                _session.SwitchChanged -= OnSwitchChanged;
                _session.SlotChanged -= OnSlotChanged;
                _session.ResponseReceived -= OnResponseReceived;
                _session.DataFrameParsed -= OnDataFrameParsed;
                _session.AudioSeqJump -= OnSeqJump;
                _session.Diagnostics -= OnDiagnostics;
            }
            _session = value;
            if (_session != null)
            {
                _session.AudioFrameReady += OnAudioFrameReady;
                _session.KeyEvent += OnKeyEvent;
                _session.KeyAction += OnKeyAction;
                _session.MouseButton += OnMouseButton;
                _session.MouseFrame += OnMouseFrame;
                _session.SwitchChanged += OnSwitchChanged;
                _session.SlotChanged += OnSlotChanged;
                _session.ResponseReceived += OnResponseReceived;
                _session.DataFrameParsed += OnDataFrameParsed;
                _session.AudioSeqJump += OnSeqJump;
                _session.Diagnostics += OnDiagnostics;
            }
        }
    }

    /// <summary>启动载历史目录（仪表盘同款存储：BLE 扫描 + 串口 0x51 读过的 MAC 都在）。</summary>
    private async Task LoadKnownPensAsync()
    {
        try
        {
            var pens = await _penBinding.LoadKnownPensAsync();
            RefreshPenOptions(pens);
            if (pens.Count > 0)
                Log($"已载入已知笔端目录 {pens.Count} 支（与仪表盘同款：扫描+串口 0x51 两源一份存储）");
        }
        catch (Exception ex)
        {
            Log($"载入已知笔端目录失败：{ex.Message}");
        }
    }

    private void RefreshPenOptions(IReadOnlyList<KnownPen> pens)
    {
        var keep = SelectedPen?.MacDisplayText;
        PenOptions.Clear();
        foreach (var p in pens)
            PenOptions.Add(p);
        // 优先保住原选中；否则默认选目录第一名（排序已按 层级＞在场＞RSSI）
        SelectedPen = PenOptions.FirstOrDefault(p =>
            string.Equals(p.MacDisplayText, keep, StringComparison.OrdinalIgnoreCase))
            ?? PenOptions.FirstOrDefault();
    }

    /// <summary>扫描（5s）：SunVote 笔端广播并入已知目录（仪表盘同款），下拉随之刷新。</summary>
    [RelayCommand]
    private async Task ScanDevicesAsync()
    {
        if (!CanScan) return;
        IsScanningDevices = true;
        try
        {
            Log("扫描 5s 找笔端广播（并入已知目录）…");
            var pens = await _penBinding.ScanPensAsync(5);
            RefreshPenOptions(pens);
            var present = pens.Count(p => p.IsPresent);
            Log(present > 0
                ? $"扫描完成：在场 {present} 支 / 目录共 {pens.Count} 支，默认选中 {SelectedPen?.MacDisplayText}"
                : $"扫描完成：无笔端广播（键盘连着基站时不广播；目录仍存 {pens.Count} 支历史可选）");
        }
        catch (Exception ex)
        {
            Log($"扫描失败：{ex.Message}");
        }
        finally
        {
            IsScanningDevices = false;
        }
    }

    /// <summary>连接（与扫描分开）：连下拉选中的笔端 MAC（起连前快照 MAC/名字，防连接期间换选串台）。</summary>
    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (!CanConnect || SelectedPen == null) return;
        var mac = SelectedPen.MacDisplayText;
        var label = SelectedPen.LocalName ?? SelectedPen.EffectiveBtName;
        IsBusy = true;
        try
        {
            var session = new SoftReceiverSession(_central);
            Session = session;
            await session.ConnectAsync(mac);
            IsConnected = true;
            ConnectionText = $"已连接 {mac}（{label}）";
            _ = RefreshAudioTargetsAsync();   // 连接后再刷一次输出/桥目标候选（8i：设备可能中途插拔）
            Log($"svr1 特征：DATA={(session.DataCharacteristicFound ? "✓" : "✗")} " +
                $"CMD={(session.CmdCharacteristicFound ? "✓" : "✗")} " +
                $"RSP={(session.RspCharacteristicFound ? "✓" : "✗")}");

            var cts = new CancellationTokenSource();
            _subscribeCts = cts;
            _ = Task.Run(async () =>
            {
                try
                {
                    await session.StartAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // 正常收尾
                }
                catch (Exception ex)
                {
                    Log($"收流异常：{ex.Message}");
                }
            });
            Log("收流中…（语音流触发=物理 K2 语音键 或 S1 扩音档）");
        }
        catch (Exception ex)
        {
            Log($"连接失败：{ex.Message}");
            Session = null;
            IsConnected = false;
            ConnectionText = "未连接";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private CancellationTokenSource? _subscribeCts;

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (!IsConnected && Session == null) return;
        IsBusy = true;
        try
        {
            if (_probeCts != null)
            {
                // 实测中直接断开：取消等待窗，probe 任务自行收尾（停播/分析/清场）
                _probeCts.Cancel();
                _probeCts = null;
            }
            if (_subscribeCts != null)
            {
                _subscribeCts.Cancel();
                _subscribeCts.Dispose();
                _subscribeCts = null;
            }
            if (Session != null)
                await Session.StopAsync();
            await _central.DisconnectAsync();
        }
        catch (Exception ex)
        {
            Log($"断开异常：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
            IsConnected = false;
            ConnectionText = "未连接";
            BatteryText = ChargeText = DevStateText = ModeText = "—";
            StatsText = "—";
            if (IsSpeakerOn)
                ToggleSpeakerExecute();
            if (IsBridgeOn)
                StopBridge("断开");
            if (IsRecording)
                StopRecord();
            if (IsKeyActionsOn)
            {
                _keyActions = null;
                IsKeyActionsOn = false;
                KeyActionsText = "关";
                Log("软件键鼠已随断开自动关");
            }
            _lastSwitchMode = 0xFF;
            _speakerStatsTimer?.Stop();
        }
    }

    /// <summary>扩音泵出开关：解码 PCM 直入扩音引擎外源模式（替代 WASAPI 采集）→ 扬声器。</summary>
    [RelayCommand]
    private void ToggleSpeaker()
    {
        if (IsProbing)
        {
            Log("[延迟实测] 进行中，不能开关扬声器（等实测结束或断开）");
            return;
        }
        if (IsSpeakerOn)
        {
            ToggleSpeakerExecute();
            return;
        }
        if (!IsConnected && !IsHidConnected)
        {
            Log("先连接键盘（或连接 EP4 直送）再开扬声器");
            return;
        }
        ToggleSpeakerExecute();
    }

    private void ToggleSpeakerExecute()
    {
        if (IsSpeakerOn)
        {
            _speakerClock.Reset();
            if (_exclusiveEngine != null)
                StopExclusiveEngine();
            else
                _ = _passthrough.StopAsync();
            IsSpeakerOn = false;
            SpeakerText = "关";
            Log("扩音泵出已关");
            MaybeStopStatsTimer();
            return;
        }
        try
        {
            if (IsExclusiveRender)
            {
                // 独占渲染（M3）：端点独占格式直出（默认渲染设备；无 AEC/ANS 处理链）
                _exclusiveEngine = new ExclusiveRenderEngine();
                using (var enumerator = new MMDeviceEnumerator())
                {
                    var render = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    _exclusiveEngine.Start(render, (float)SoftwareGain, 20);
                }
                var rep = _exclusiveEngine.GetReport();
                IsSpeakerOn = true;
                SpeakerText = "开（独占）";
                _speakerClock.Restart();
                EnsureStatsTimer();
                Log($"独占渲染已开：[{rep.DeviceName}] {rep.FormatText}" +
                    (rep.Exclusive ? "" : $"——回退原因：{rep.FallbackReason}（链路先通、引擎税后收）") +
                    $"；增益 {SoftwareGain:F1}x 滑条可即调；独占=独占端点，期间本机其他声音被顶掉；无 AEC/ANS");
                return;
            }
            _ = _passthrough.StartAsync(new PassthroughStartOptions
            {
                ExternalSource = true,
                RenderDeviceId = ResolveSpeakerRenderId(),   // 8i：输出可选，null=系统默认
                RenderLatencyMs = 40,
                Volume = (float)SoftwareGain,   // 11-03：软件增益滑条（默认 50≈4.9x，运行中可即调）
                EnableAec = true,
                EnableAns = true,
                EnableAfc = false,
                HpfHz = 80,
            });
            IsSpeakerOn = true;
            SpeakerText = "开（外源泵 16k）";
            _speakerClock.Restart();
            EnsureStatsTimer();
            Log($"扩音泵出已开（16k 外源泵→处理链→扬声器，软件增益 {SoftwareGain:F1}x，滑条可即调）");
        }
        catch (Exception ex)
        {
            Log($"扩音启动失败：{ex.Message}");
            StopExclusiveEngine();
        }
    }

    private DispatcherTimer CreateSpeakerStatsTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            var ex = _exclusiveEngine;
            if (ex != null)
            {
                // 独占渲染心跳：格式+欠载（+HID 直送通道统计同行带出）
                var line = $"独占渲染 {_speakerClock.Elapsed.TotalSeconds:F0}s：[{ex.DeviceName}] {ex.FormatText} 欠载 {ex.Underruns}" +
                           (ex.FallbackReason != null ? $"（回退：{ex.FallbackReason}）" : "");
                if (IsHidSource && _hidSession != null)
                    StatsText = $"{line}；{_hidSession.Summary()}";
                else
                    StatsText = line;
            }
            else if (IsHidSource && _hidSession != null)
            {
                // HID 直送心跳：解码帧计数 + 通道统计（音频/F5/缺口各路独立计数）
                var frames = Interlocked.Read(ref _hidFrameCount);
                StatsText = $"HID 直送：解码帧 {frames}（{frames * 7.5 / 1000.0:F1}s @7.5ms/帧）；{_hidSession.Summary()}";
            }
            else
            {
                var s = _passthrough.GetStats();
                StatsText = $"泵出运行 {_speakerClock.Elapsed.TotalSeconds:F0}s：" +
                            $"环深 {s.RingDepthMs:F0}ms / 估计延迟 {s.EstimatedLatencyMs:F0}ms / 欠载 {s.UnderrunEvents}";
            }
            var b = _bridge;
            BridgeInfoText = b == null ? "—" :
                $"环深 {b.RingDepthMs:F0}ms / 泵入 {b.PumpedSamples / 16000.0:F1}s / 欠载块 {b.UnderrunBlocks}";
        };
        return timer;
    }

    private void EnsureStatsTimer()
    {
        _speakerStatsTimer ??= CreateSpeakerStatsTimer();
        _speakerStatsTimer.Start();
    }

    private void MaybeStopStatsTimer()
    {
        if (!IsSpeakerOn && _bridge == null && !(IsHidSource && IsHidConnected))
            _speakerStatsTimer?.Stop();
    }

    /// <summary>独占引擎健全清场（断开/异常路径用）：有就 Dispose 置空。</summary>
    private void StopExclusiveEngine()
    {
        if (_exclusiveEngine == null)
            return;
        _exclusiveEngine.Dispose();
        _exclusiveEngine = null;
    }

    // ---- 延迟实测（chirp 回环） ----

    /// <summary>
    /// 延迟实测：扬声器播 chirp 训练列 → 笔端麦拾取 → 回传（BLE 会话或 EP4 HID 直送，按当前数据源）
    /// → 走表剖面 + chirp 扫描 + WAV 留档。复用 <see cref="SoftReceiverProbe"/>（CLI 同一份逻辑，
    /// 防双拷漂移）；开测自动关扩音（防 chirp 混叠/啸叫）；语音流须在流（K2/S1 物理触发）。
    /// </summary>
    [RelayCommand]
    private async Task ProbeAsync()
    {
        if (!CanProbe)
            return;
        if (!int.TryParse(ProbeSecondsInput, out var sec))
        {
            Log("[延迟实测] 时长需为数字（10~300 秒）");
            return;
        }
        sec = Math.Clamp(sec, 10, 300);
        var hidLeg = IsHidSource;
        if (hidLeg && _hidSession == null)
            return;
        if (!hidLeg && Session == null)
            return;

        IsProbing = true;
        var probe = hidLeg ? new SoftReceiverProbe(_hidSession!) : new SoftReceiverProbe(Session!);
        var myCts = new CancellationTokenSource();
        _probeCts = myCts;
        try
        {
            if (IsSpeakerOn)
            {
                ToggleSpeakerExecute();
                Log("[延迟实测] 自动关扩音（防 chirp 混叠/啸叫）");
            }
            if (IsBridgeOn)
            {
                StopBridge("延迟实测前自动关");
                Log("[延迟实测] 自动关虚拟麦克风桥（chirp 勿外泄给第三方）");
            }
            probe.Start(1.0f, ResolveSpeakerRenderId());
            Log($"[延迟实测] chirp 已开播 → [{probe.RenderDeviceName}]，收流 {sec}s" +
                $"（{(hidLeg ? "HID 直送腿" : "BLE 腿")}；语音流须在流：K2/S1 物理触发）…");
            ProbeResultText = $"实测中…（{sec}s，{(hidLeg ? "HID 直送腿" : "BLE 腿")}；期间按 K2 语音键或拨 S1 扩音档让语音流在流）";
            await Task.Delay(TimeSpan.FromSeconds(sec), myCts.Token);
        }
        catch (OperationCanceledException)
        {
            Log("[延迟实测] 已提前中止（断开或取消）");
        }
        catch (Exception ex)
        {
            Log($"[延迟实测] 异常：{ex.Message}");
        }
        finally
        {
            SoftReceiverProbeReport? report = null;
            try
            {
                report = probe.StopAndAnalyze(BuildProbeWavPath());
            }
            catch (Exception ex)
            {
                Log($"[延迟实测] 收尾异常：{ex.Message}");
            }
            myCts.Dispose();
            if (ReferenceEquals(_probeCts, myCts))
                _probeCts = null;
            IsProbing = false;
            if (report != null)
                ShowProbeReport(report);
            else
                ProbeResultText = (hidLeg ? CanProbeHid : IsConnected)
                    ? "（未测出：解码音频帧不足——语音流未在流，K2/S1 物理触发后再试）"
                    : "（未测：已断开/未就绪中止）";
        }
    }

    private void ShowProbeReport(SoftReceiverProbeReport r)
    {
        var ppm = r.Scan.Ppm?.ToString("F0") ?? "—";
        ProbeResultText =
            $"走表：步长 {r.Pacing.FittedStepMs:F3}ms（PPM {r.Pacing.Ppm:F0}）｜" +
            $"偏差 p50 {r.Pacing.P50DevMs:F1} / p95 {r.Pacing.P95DevMs:F1} / max {r.Pacing.MaxDevMs:F1} ms（{r.Pacing.Frames} 帧）\n" +
            $"chirp：检出 {r.Scan.DetectedCount}/{r.Scan.ExpectedCount}" +
            (r.Scan.MissingSlots.Count > 0 ? $"（缺失 {r.Scan.MissingSlots.Count} 槽）" : "（无缺失）") +
            $"，PPM {ppm}\n" +
            $"WAV：{r.WavPath}（{r.TotalPcmBytes / 32000.0:F1}s @16k mono）\n" +
            "口径：绝对延迟含起点常量偏置（笔端自由跑钟无共享锚），同场形状/PPM 有效、跨场勿比绝对值";
        Log($"[延迟实测] 完成：走表 PPM {r.Pacing.Ppm:F0} / chirp {r.Scan.DetectedCount}/{r.Scan.ExpectedCount}；WAV={r.WavPath}");
    }

    private static string BuildProbeWavPath() =>
        Path.Combine(AppContext.BaseDirectory, "srx_probe", $"srx_probe_{DateTime.Now:yyyyMMdd_HHmmss}.wav");

    // ---- 虚拟麦克风桥（R1PBleAudio 桥式腿） ----

    /// <summary>
    /// 开/关桥：解码 PCM → 虚拟声卡渲染端，第三方软件从同卡采集端当麦克风录音。
    /// 复用 <see cref="SrxBridgePump"/>（CLI srx bridge 同一份逻辑）；设备枚举每次开桥时刷新。
    /// </summary>
    [RelayCommand]
    private void ToggleBridge()
    {
        if (IsBridgeOn)
        {
            StopBridge("手动关桥");
            return;
        }
        if (!IsConnected && !IsHidConnected)
        {
            Log("先连接键盘（或连接 EP4 直送）再开桥");
            return;
        }
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var renders = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
            var captures = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
            MMDevice renderDev;
            MMDevice captureDev;
            if (SelectedBridgeTarget is null || SelectedBridgeTarget == AutoBridgeTarget)
            {
                // 自动配对（SelectPair：非 UU 优先）
                var (ri, ci) = SrxBridgePump.SelectPair(
                    renders.Select(d => d.FriendlyName).ToList(), captures.Select(d => d.FriendlyName).ToList());
                if (ri < 0)
                {
                    Log("未发现虚拟声卡配对端点（CABLE Input/Output 类）——装 VB-Cable 后重试，或在「虚拟麦克风」下拉手动选桥目标");
                    return;
                }
                renderDev = renders[ri];
                captureDev = captures[ci];
            }
            else
            {
                // 手动指定桥目标（8i）：按名字找活动播放设备，录音口=同族括注匹配
                renderDev = renders.FirstOrDefault(d => d.FriendlyName == SelectedBridgeTarget)
                    ?? throw new InvalidOperationException($"桥目标 [{SelectedBridgeTarget}] 不在当前活动播放设备列表");
                captureDev = captures.FirstOrDefault(d =>
                    SrxBridgePump.NameCandidatesMatch(renderDev.FriendlyName, d.FriendlyName))
                    ?? throw new InvalidOperationException(
                        $"桥目标 [{renderDev.FriendlyName}] 无同族录音端点——第三方软件拿不到对应麦克风");
            }
            var pump = new SrxBridgePump();
            pump.Start(renderDev, 1.0f);
            _bridge = pump;
            IsBridgeOn = true;
            BridgeText = "开";
            BridgeDeviceText = captureDev.FriendlyName;
            EnsureStatsTimer();
            Log($"桥已开：解码 PCM → [{renderDev.FriendlyName}]；第三方软件选麦克风「{captureDev.FriendlyName}」即 R1 人声（增量≈15~50ms）");
            if (SrxBridgePump.IsUuShellName(renderDev.FriendlyName) ||
                SrxBridgePump.IsUuShellName(captureDev.FriendlyName))
            {
                Log("⚠ 桥目标=UU远程外壳（第三方软件装的虚拟声卡，依赖其客户端转发，非 PencilBox 设备）——" +
                    "正式对外请安装 VB-Cable，装完重开桥会自动改选 CABLE Input");
            }
        }
        catch (Exception ex)
        {
            Log($"桥启动失败：{ex.Message}");
        }
    }

    private void StopBridge(string reason)
    {
        var pump = _bridge;
        _bridge = null;
        if (pump != null)
        {
            var pumped = pump.PumpedSamples / 16000.0;
            pump.Dispose();
            Log($"桥已关（{reason}）：本场泵入 {pumped:F1}s 音频");
        }
        IsBridgeOn = false;
        BridgeText = "关";
        BridgeDeviceText = "—";
        BridgeInfoText = "—";
        MaybeStopStatsTimer();
    }

    // ---- 软件键鼠（键盘腿 Keymap 表驱动 + 鼠标腿空鼠直通，与 CLI --actions 同一份执行器） ----

    /// <summary>
    /// 开/关软件键鼠：委托 <see cref="SrxKeyActionExecutor"/>（CLI <c>srx receive --actions</c> 同一份逻辑）。
    /// 键盘腿=0x13 语义键 + S1 档位查 Keymap 出厂表 → Win32 注入进前台焦点；
    /// 鼠标腿=空鼠槽原始量直通（相对位移/滚轮/键沿）。默认关；断开自动关。
    /// </summary>
    [RelayCommand]
    private void ToggleKeyActions()
    {
        if (IsKeyActionsOn)
        {
            _keyActions = null;
            IsKeyActionsOn = false;
            KeyActionsText = "关";
            Log("软件键鼠已关（键/空鼠不再注入，事件流继续显示）");
            return;
        }
        if (IsHidSource)
        {
            Log("HID 直送路无键动作（0x13 语义键/空鼠槽走笔端 GATT，不经基站 EP4 转发）——软件键鼠仅 BLE 直连可用");
            return;
        }
        if (!IsConnected || Session == null)
        {
            Log("先连接键盘再开软件键鼠");
            return;
        }
        var executor = new SrxKeyActionExecutor(
            msg => AppendLog(msg),
            msg => AppendLog($"⚠ {msg}"));
        if (_lastSwitchMode != 0xFF)
            executor.SeedSwitchBaseline(_lastSwitchMode);   // 中途开启：预置档位基线防假切换
        _keyActions = executor;
        IsKeyActionsOn = true;
        KeyActionsText = "开";
        Log("软件键鼠已开（Keymap 出厂表）：K3/K4=翻页 K5/K6=音量 K2=F13/F14 会话边界 S1=F15/F16；" +
            "空鼠=相对移动/左右键/滚轮直注，注入进前台焦点（点哪算哪，注意当前窗口）");
    }

    // ---- 收流录制 WAV（receive 腿） ----

    /// <summary>录制/停止：解码 PCM 直落 WAV（16k/16bit/mono），存 exe 目录 srx_record\。</summary>
    [RelayCommand]
    private void ToggleRecord()
    {
        if (IsRecording)
        {
            StopRecord();
            return;
        }
        if (!IsConnected && !IsHidConnected)
        {
            Log("先连接键盘（或连接 EP4 直送）再录制");
            return;
        }
        if (IsConnected && Session == null)
            return;
        var dir = Path.Combine(AppContext.BaseDirectory, "srx_record");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"srx_record_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
        try
        {
            _recordWriter = new WaveFileWriter(path, new WaveFormat(MsbcDecoder.SampleRate, 16, 1));
            _recordFrames = 0;
            _recordSw = Stopwatch.StartNew();
            IsRecording = true;
            RecordText = "停止录制";
            Log($"开始录制 WAV → {path}（16k/16bit/mono；语音流须在流）");
        }
        catch (Exception ex)
        {
            Log($"录制启动失败：{ex.Message}");
        }
    }

    private void StopRecord()
    {
        WaveFileWriter? writer;
        lock (_recordLock)
        {
            writer = _recordWriter;
            _recordWriter = null;
        }
        _recordSw?.Stop();
        IsRecording = false;
        RecordText = "录制 WAV";
        if (writer == null)
            return;
        var seconds = _recordFrames * 7.5 / 1000.0;
        try { writer.Dispose(); } catch { /* 收尾容错 */ }
        Log($"录制完成：{_recordFrames} 帧 ≈ {seconds:F1}s（{Path.GetFileName(writer.Filename)}）");
    }

    // ---- 会话事件 → UI（WinRT 线程池回调，一律调度回 UI 线程） ----

    private void OnAudioFrameReady(object? sender, SoftReceiverAudioFrame frame)
    {
        if (IsSpeakerOn)
        {
            if (_exclusiveEngine != null)
                _exclusiveEngine.WritePcm(frame.Pcm);
            else
                _ = _passthrough.WriteExternalPcm(frame.Pcm);
        }
        _bridge?.WritePcm(frame.Pcm);
        lock (_recordLock)
        {
            if (_recordWriter != null)
            {
                _recordWriter.Write(frame.Pcm);
                _recordFrames++;
            }
        }
    }

    private void OnKeyEvent(object? sender, SoftReceiverKeyEvent k) =>
        AppendLog($"键 {(k.IsDown ? "DOWN" : "UP")} page=0x{k.Page:X2} code=0x{k.KeyCode:X2} mod=0x{k.Modifier:X2}");

    // 0x13 语义键（DONGLE_MODE=1 笔端实发）：软件键鼠开=转发执行器（查表注入+逐条日志），关=只显示
    private void OnKeyAction(object? sender, SoftReceiverKeyActionEvent a)
    {
        var executor = _keyActions;
        if (executor != null)
        {
            executor.HandleKeyAction(a);   // 执行器日志经 AppendLog 上 UI 线程
            return;
        }
        AppendLog($"键动作 {SrxKeyActionRouter.KeyName(a.KeyNum)} {SrxKeyActionRouter.ActionName(a.Action)}（key={a.KeyNum} act=0x{a.Action:X2}）");
    }

    private void OnMouseButton(object? sender, SoftReceiverMouseButtonEvent m)
    {
        var executor = _keyActions;
        if (executor != null)
        {
            executor.HandleMouseButton(m.Left, m.Right);
            return;
        }
        AppendLog($"鼠标键沿 左={(m.Left ? "按下" : "释放")} 右={(m.Right ? "按下" : "释放")}（空鼠 K3/K4）");
    }

    /// <summary>空鼠槽逐帧（50Hz）：只转发执行器注入，不出日志（会刷爆事件流）；采样前 8 帧由执行器露线。</summary>
    private void OnMouseFrame(object? sender, SoftReceiverMouseFrame m) => _keyActions?.HandleMouseFrame(m);

    private void OnSwitchChanged(object? sender, SoftReceiverSwitchEvent s)
    {
        _lastSwitchMode = s.Mode;   // 记档位，供中途开软件键鼠预置基线
        _keyActions?.HandleSwitch(s.Mode);   // 变化沿才注入（基线/去重执行器自管），日志经 AppendLog
        MainThreadHelper.EnsureOnMainThread(() =>
        {
            ModeText = s.Mode switch
            {
                0 => "IDLE",
                1 => "识别",
                2 => "扩音",
                _ => $"0x{s.Mode:X2}"
            };
        });
        AppendLog($"档位切换 → {s.Mode}");
    }

    private void OnSlotChanged(object? sender, SoftReceiverSlotEvent e)
    {
        if (e.Id == PenStateSlotId.Batt)
        {
            MainThreadHelper.EnsureOnMainThread(() => BatteryText = $"{e.Value}%");
        }
        else if (e.Id == PenStateSlotId.DevState)
        {
            MainThreadHelper.EnsureOnMainThread(() => DevStateText = e.Value switch
            {
                (byte)PenDevState.Normal => "正常",
                (byte)PenDevState.Sleep => "休眠",
                _ => $"0x{e.Value:X2}"
            });
        }
        AppendLog($"槽 0x{e.Id:X2} → {e.Value}");
    }

    private void OnResponseReceived(object? sender, SoftReceiverRspEvent r) =>
        AppendLog($"RSP cmd=0x{r.CmdId:X2} status={r.Status}");

    private void OnDataFrameParsed(object? sender, VpDataFrame frame)
    {
        // 音频流时 DATA 帧 ~133帧/s：充电态只在变化时刷，统计行 500ms 节流（别把 UI 线程打满）
        var prev = Interlocked.Exchange(ref _lastChargeState, frame.ChargeState);
        if (prev != frame.ChargeState)
        {
            MainThreadHelper.EnsureOnMainThread(() => ChargeText = PenStatusText.ChargeStateText(frame.ChargeState));
        }
        var now = Environment.TickCount64;
        if (now - _lastStatsUiMs < 500)
            return;
        Interlocked.Exchange(ref _lastStatsUiMs, now);
        var summary = Session?.Stats.Summary();
        MainThreadHelper.EnsureOnMainThread(() => StatsText = summary ?? "—");
    }

    private long _lastStatsUiMs;
    /// <summary>充电态防重（0xFF=首帧必刷哨兵）</summary>
    private long _lastChargeState = 0xFF;

    private void OnSeqJump(object? sender, long gap) =>
        AppendLog($"seq 跳变缺 {gap} 帧（掩盖为静音）");

    private void OnDiagnostics(object? sender, string msg) => Log(msg);

    private void AppendLog(string line) =>
        MainThreadHelper.EnsureOnMainThread(() =>
        {
            EventLog.Add($"{DateTime.Now:HH:mm:ss.fff}  {line}");
            while (EventLog.Count > EventLogCapacity)
                EventLog.RemoveAt(0);
        });

    private void Log(string line) => AppendLog(line);
}
