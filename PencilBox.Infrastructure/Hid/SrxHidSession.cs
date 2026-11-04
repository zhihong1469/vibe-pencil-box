// <copyright file="SrxHidSession.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Ble.SoftReceiver;
using PencilBox.Core.Communication;

namespace PencilBox.Infrastructure.Hid;

/// <summary>HID 直送解码音频帧（16k/16bit/mono，240B=7.5ms；合成静音帧带标记）。</summary>
/// <param name="Pcm">解码 PCM（合成帧=全零）</param>
/// <param name="Counter">设备帧计数（合成帧≈当前计数）</param>
/// <param name="IsSynthetic">true=计数缺口补的静音帧（非真实上行）</param>
/// <param name="LocalTimestampMs">到达时刻（Environment.TickCount64，PC 本地钟）</param>
public sealed record SrxHidAudioFrame(byte[] Pcm, ushort Counter, bool IsSynthetic, long LocalTimestampMs);

/// <summary>
/// HID 直送 mSBC 会话（08 号方案 M1 会话件）：路由控制（0x56）+ 解码 + 缺口插静音。
/// <list type="bullet">
/// <item>路由：下行 <c>F5 AA AA 04 61 01 56 [route]</c>（0=UAC 1=HID），等 E1 应答（TCS 2s 超时）；
///       应答经 ReadFile 到达本身即证明读线程在消费——时序门（08 号 §2"先读后切"）由此自然满足。</item>
/// <item>回退：E0 看门狗事件（基站音频环溢出 ≥24 帧 ≈180ms 自动回 UAC）→ <see cref="IsRoutedHid"/>=false + 事件上抛。</item>
/// <item>解码：每报告 60B 直喂 <see cref="MsbcDecoder.DecodeWrapped"/>（HID 60B=[0x01][类型][57B SBC][0x00]
///       与既有包格式兼容，byte[1] 语义差异仅影响诊断位）。</item>
/// <item>缺口：u16 帧计数环形差 gap &gt; 0 → 补 gap×240B 全零静音帧（7.5ms/帧，08 号 §3.4）。</item>
/// </list>
/// 音频事件在 HID 读线程回调；消费方（泵/录制/统计）自行加锁，与 BLE 腿 <see cref="SoftReceiverSession"/>
/// 的 AudioFrameReady 同形状（合成静音帧也照常上抛，保节拍连续）。
/// </summary>
public sealed class SrxHidSession : IDisposable
{
    /// <summary>0x56 实验号（0x55 已被 keymap 占用；08 号 §4）</summary>
    public const byte RouteCmd = 0x56;

    /// <summary>E1 应答 / E0 看门狗回退事件</summary>
    public const byte RouteCmdResp = 0xE1;
    public const byte RouteWatchdogEvent = 0xE0;

    /// <summary>STATUS：OK / 参数错 / 本固件无该能力（DG_AUD_HID_ENABLE 宏未开）</summary>
    public const byte StatusOk = 0x00;
    public const byte StatusBadParam = 0x01;
    public const byte StatusNoCapability = 0xFF;

    private readonly IHidTransport _transport;
    private readonly HidAudioChannel _channel;
    private readonly MsbcDecoder _decoder = new();
    private readonly Action<string>? _log;
    private TaskCompletionSource<HidRouteControlFrameEventArgs>? _routeTcs;
    private long _decodedFrames, _syntheticFrames, _crcErrors, _syncErrors, _unknownStatus;
    private bool _disposed;

    public SrxHidSession(IHidTransport transport, Action<string>? log = null)
    {
        _transport = transport;
        _log = log;
        _channel = new HidAudioChannel(transport);
        _channel.AudioReportReceived += OnAudioReport;
        _channel.CounterGap += OnCounterGap;
        _channel.RouteControlFrame += OnRouteControlFrame;
    }

    /// <summary>解码音频帧到达（HID 读线程回调；含缺口补的合成静音帧）</summary>
    public event EventHandler<SrxHidAudioFrame>? AudioFrameReady;

    /// <summary>看门狗回退（基站环溢出自动回 UAC）——订阅方应停泵/改状态显示</summary>
    public event EventHandler? WatchdogFallback;

    /// <summary>0x56 路由应答 STATUS=0x01（参数错）</summary>
    public const string BadParamHint = "参数错（route 值非法？）";
    /// <summary>0x56 路由应答 STATUS=0xFF</summary>
    public const string NoCapabilityHint = "本固件无 HID 直送能力（DG_AUD_HID_ENABLE 宏未开，须烧 test/hid 构建包）";

    /// <summary>当前是否已切入 HID 直送（E0 看门狗回退会自动翻回 false）</summary>
    public bool IsRoutedHid { get; private set; }

    public HidAudioChannelStats ChannelStats => _channel.Snapshot();

    /// <summary>切入 HID 直送（route=1）。返回 false=无应答/拒绝，详情进日志；true 后音频报告开始到达。</summary>
    public Task<bool> RouteToHidAsync(int timeoutMs = 2000, CancellationToken ct = default)
        => RouteAsync(route: 0x01, timeoutMs, ct);

    /// <summary>切回 UAC 默认路（route=0）。幂等安全；OTA 会话启动前建议主动调一次（08 号 §4）。</summary>
    public Task<bool> RouteToUacAsync(int timeoutMs = 2000, CancellationToken ct = default)
        => RouteAsync(route: 0x00, timeoutMs, ct);

    private async Task<bool> RouteAsync(byte route, int timeoutMs, CancellationToken ct)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SrxHidSession));
        if (!_transport.IsConnected)
            throw new InvalidOperationException("HID 通道未连接（先连接基站 EP4 命令通道）");

        // 下行逻辑帧：F5 AA AA 04 61 01 56 [route]，补零到 64B 定长承载
        var report = new byte[HidAudioChannel.ReportLength];
        new byte[] { 0xF5, 0xAA, 0xAA, 0x04, 0x61, 0x01, RouteCmd, route }.CopyTo(report, 0);

        // RunContinuationsAsynchronously：应答在 HID 读线程 TrySetResult，续延勿回原线程跑
        var tcs = new TaskCompletionSource<HidRouteControlFrameEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _routeTcs = tcs;
        try
        {
            // 时序门（08 号 §2）：先确认读循环在消费（连接即已起读线程）再发路由；
            // OK 应答本身经 ReadFile 到达 = 读线程在消费的活证明。
            await _transport.SendReportAsync(0, report, ct);

            var winner = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs, ct)).ConfigureAwait(false);
            if (winner != tcs.Task)
            {
                _log?.Invoke($"[SRX-HID] 0x56 route={route} 应答超时（{timeoutMs}ms）——读线程未消费或固件无应答；180ms 后基站可能自行回 UAC");
                return false;
            }

            var resp = tcs.Task.Result;
            if (resp.CommandType != RouteCmdResp)
                return false;   // E0 不作应答（OnRouteControlFrame 只对 E1 设结果，此为兜底）

            if (resp.Status == StatusOk)
            {
                IsRoutedHid = route == 0x01;
                _log?.Invoke($"[SRX-HID] 0x56 应答 OK：route={(IsRoutedHid ? "HID 直送" : "UAC 默认")}（回读一致）");
                if (!IsRoutedHid)
                    ResetDecodeCounters();
                return true;
            }

            _log?.Invoke($"[SRX-HID] 0x56 应答 STATUS=0x{resp.Status:X2}" +
                         (resp.Status == StatusBadParam ? $"（{BadParamHint}）"
                          : resp.Status == StatusNoCapability ? $"（{NoCapabilityHint}）"
                          : "（未知状态）"));
            return false;
        }
        finally
        {
            _routeTcs = null;
        }
    }

    private void OnRouteControlFrame(object? sender, HidRouteControlFrameEventArgs e)
    {
        if (e.CommandType == RouteCmdResp)
        {
            _routeTcs?.TrySetResult(e);
            return;
        }

        // E0 看门狗回退事件：基站音频环溢出（≥24 帧 ≈180ms）已自动回 UAC
        if (IsRoutedHid)
        {
            IsRoutedHid = false;
            ResetDecodeCounters();
            _log?.Invoke("[SRX-HID] 看门狗回退：基站音频环溢出（E0 01 56），基站已自动回 UAC——读线程吞吐或链路质量存疑");
            WatchdogFallback?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnAudioReport(object? sender, HidAudioReportEventArgs e)
    {
        if (_disposed)
            return;
        var pcm = new byte[MsbcDecoder.PcmBytesPerFrame];
        var status = _decoder.DecodeWrapped(e.Frame60, pcm, out _);
        switch (status)
        {
            case MsbcDecodeStatus.Ok:
                Interlocked.Increment(ref _decodedFrames);
                AudioFrameReady?.Invoke(this,
                    new SrxHidAudioFrame(pcm, e.Counter, IsSynthetic: false, Environment.TickCount64));
                break;
            case MsbcDecodeStatus.CrcMismatch:
                Interlocked.Increment(ref _crcErrors);
                break;
            case MsbcDecodeStatus.BadSyncword:
                Interlocked.Increment(ref _syncErrors);
                break;
            default:
                Interlocked.Increment(ref _unknownStatus);
                break;
        }
    }

    private void OnCounterGap(object? sender, HidCounterGapEventArgs e)
    {
        if (_disposed)
            return;
        _log?.Invoke($"[SRX-HID] 帧计数缺口 {e.LastCounter}→{e.CurrentCounter}：丢 {e.GapFrames} 帧，补静音");
        for (int i = 0; i < e.GapFrames; i++)
        {
            Interlocked.Increment(ref _syntheticFrames);
            AudioFrameReady?.Invoke(this,
                new SrxHidAudioFrame(new byte[MsbcDecoder.PcmBytesPerFrame], e.CurrentCounter,
                    IsSynthetic: true, Environment.TickCount64));
        }
    }

    private void ResetDecodeCounters()
    {
        Interlocked.Exchange(ref _decodedFrames, 0);
        Interlocked.Exchange(ref _syntheticFrames, 0);
        Interlocked.Exchange(ref _crcErrors, 0);
        Interlocked.Exchange(ref _syncErrors, 0);
        Interlocked.Exchange(ref _unknownStatus, 0);
    }

    /// <summary>统计一行（解码/补帧/误码 + 通道计数）。</summary>
    public string Summary()
    {
        var ch = _channel.Snapshot();
        return $"HID 直送：解码 {Interlocked.Read(ref _decodedFrames)} 帧" +
               $"（补静音 {Interlocked.Read(ref _syntheticFrames)}，CRC 错 {Interlocked.Read(ref _crcErrors)}，" +
               $"同步错 {Interlocked.Read(ref _syncErrors)}）；通道：{ch}";
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _routeTcs?.TrySetCanceled();
        _channel.AudioReportReceived -= OnAudioReport;
        _channel.CounterGap -= OnCounterGap;
        _channel.RouteControlFrame -= OnRouteControlFrame;
        _channel.Dispose();
    }
}
