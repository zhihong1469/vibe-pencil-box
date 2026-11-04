// <copyright file="HidAudioChannel.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Communication;
using PencilBox.Core.Protocol.Company;

namespace PencilBox.Infrastructure.Hid;

/// <summary>EP4 音频报告事件参数（08 号 §3：64B 定长，[0..59]=mSBC 60B，[60..61]=u16 LE 帧计数）。</summary>
/// <param name="Counter">设备自由跑帧计数（mod 65536，丢帧照样占号）</param>
/// <param name="Frame60">mSBC 帧 60B 快照（[0x01][类型][57B SBC][0x00]）</param>
/// <param name="Timestamp">到达时刻</param>
public sealed record HidAudioReportEventArgs(ushort Counter, byte[] Frame60, DateTime Timestamp);

/// <summary>帧计数缺口事件（gap = 上一计数到当前计数之间丢失的帧数）。</summary>
/// <param name="LastCounter">缺口前计数</param>
/// <param name="CurrentCounter">当前计数</param>
/// <param name="GapFrames">丢失帧数（&gt;=1）</param>
public sealed record HidCounterGapEventArgs(ushort LastCounter, ushort CurrentCounter, int GapFrames);

/// <summary>0x56 路由控制帧事件（下行 61/E1 应答、E0 看门狗回退事件，08 号 §4）。</summary>
/// <param name="CommandType">0xE1=应答 / 0xE0=看门狗回退事件</param>
/// <param name="Status">E1：0x00=OK 0x01=参数错 0xFF=本固件无该能力（宏未开）；E0：0x01=环溢出</param>
/// <param name="Route">E1 回读路由：0x00=UAC 0x01=HID</param>
/// <param name="Logical">完整 F5 逻辑帧（去补零后）</param>
public sealed record HidRouteControlFrameEventArgs(byte CommandType, byte Status, byte Route, byte[] Logical);

/// <summary>通道统计快照（音频/F5/丢弃/缺口各路独立计数，互不污染判读——回执 §4-#5）。</summary>
public sealed record HidAudioChannelStats(
    long AudioReports,
    long F5Reports,
    long DroppedReports,
    long GapEvents,
    long GapFrames,
    bool HasBaseline,
    ushort? LastCounter)
{
    public override string ToString() =>
        $"音频报告 {AudioReports}，F5 帧 {F5Reports}，丢弃 {DroppedReports}，缺口 {GapEvents} 次/{GapFrames} 帧" +
        (HasBaseline ? $"，计数@{LastCounter}" : "，无基线（未收音频报告）");
}

/// <summary>
/// EP4 custom HID 音频通道（08 号 HID 直送 mSBC，M1 通道件）：
/// 订阅 <see cref="IHidTransport.ReportReceived"/> 按首字节分类——
/// <list type="bullet">
/// <item><c>0x01</c> = mSBC 音频报告（60B 帧 + u16 LE 帧计数），计数缺口检测（mod 65536 环形差）；</item>
/// <item><c>0xF5</c> = 公司协议逻辑帧，仅放行 0x56 路由族（E1 应答 / E0 看门狗），其余 F5 照计不外抛；</item>
/// <item>其余 = 丢弃计数。</item>
/// </list>
/// 基线规则（08 号 §3.4）：首收音频报告即为基线，计数不因路由切换重置。
/// 与 <see cref="HidCommandChannel"/> 同事件广播共存：F5 通道对 0x01 帧快速拒绝静默丢，零互扰。
/// </summary>
public sealed class HidAudioChannel : IDisposable
{
    /// <summary>音频报告首字节标记（08 号 §3.2）</summary>
    public const byte AudioMarker = 0x01;

    /// <summary>音频报告定长 64B</summary>
    public const int ReportLength = 64;

    /// <summary>mSBC 帧长（报告 [0..59]）</summary>
    public const int MsbcFrameLength = 60;

    /// <summary>u16 LE 帧计数在报告内的偏移（[60..61]；[62..63] 保留字节不校验只丢弃）</summary>
    public const int CounterOffset = 60;

    private readonly IHidTransport _transport;
    private readonly object _lock = new();
    private bool _hasBaseline;
    private ushort _lastCounter;
    private long _audioReports, _f5Reports, _droppedReports, _gapEvents, _gapFrames;
    private bool _disposed;

    public HidAudioChannel(IHidTransport transport)
    {
        _transport = transport;
        _transport.ReportReceived += OnReportReceived;
    }

    /// <summary>mSBC 音频报告到达（HID 读线程回调，订阅方须自行保证线程安全）</summary>
    public event EventHandler<HidAudioReportEventArgs>? AudioReportReceived;

    /// <summary>帧计数缺口（丢失帧照常占号；订阅方按 gap 插 7.5ms 静音帧）</summary>
    public event EventHandler<HidCounterGapEventArgs>? CounterGap;

    /// <summary>0x56 路由族控制帧（E1 应答 / E0 看门狗回退事件）</summary>
    public event EventHandler<HidRouteControlFrameEventArgs>? RouteControlFrame;

    public bool HasBaseline { get { lock (_lock) return _hasBaseline; } }

    public ushort LastCounter { get { lock (_lock) return _lastCounter; } }

    public HidAudioChannelStats Snapshot()
    {
        lock (_lock)
        {
            return new HidAudioChannelStats(
                _audioReports, _f5Reports, _droppedReports, _gapEvents, _gapFrames,
                _hasBaseline, _hasBaseline ? _lastCounter : null);
        }
    }

    private void OnReportReceived(object? sender, HidReportReceivedEventArgs e)
    {
        if (_disposed || !_transport.IsNoReportIdDevice)
            return;

        var data = e.Data;
        if (data == null || data.Length < CounterOffset + 2)
        {
            lock (_lock) _droppedReports++;
            return;
        }

        switch (data[0])
        {
            case AudioMarker:
                OnAudioReport(data);
                break;

            case CompanyFrame.Head0 when CompanyFrame.TryTrimToLogical(data, out var logical):
                OnF5Frame(logical);
                break;

            default:
                lock (_lock) _droppedReports++;
                break;
        }
    }

    private void OnAudioReport(byte[] data)
    {
        ushort counter = (ushort)(data[CounterOffset] | (data[CounterOffset + 1] << 8));
        var frame60 = new byte[MsbcFrameLength];
        Array.Copy(data, 0, frame60, 0, MsbcFrameLength);

        lock (_lock)
        {
            if (_hasBaseline)
            {
                // 环形差：gap = (curr - prev - 1) & 0xFFFF（回绕安全；0=连续无缺口）
                int gap = (counter - _lastCounter - 1) & 0xFFFF;
                if (gap > 0)
                {
                    _gapEvents++;
                    _gapFrames += gap;
                    CounterGap?.Invoke(this, new HidCounterGapEventArgs(_lastCounter, counter, gap));
                }
            }
            else
            {
                _hasBaseline = true;   // 首报为基线（08 号 §3.4）
            }
            _lastCounter = counter;
            _audioReports++;
        }

        AudioReportReceived?.Invoke(this, new HidAudioReportEventArgs(counter, frame60, DateTime.Now));
    }

    private void OnF5Frame(byte[] logical)
    {
        lock (_lock) _f5Reports++;

        // 0x56 族：E1 应答（F5 AA AA 05 E1 01 56 STATUS route）、
        //           E0 看门狗回退事件（F5 AA AA 04 E0 01 56 01）
        if (logical.Length >= 8 && logical[5] == 0x01 && logical[6] == 0x56 &&
            logical[4] is 0xE1 or 0xE0)
        {
            byte status = logical.Length > 7 ? logical[7] : (byte)0;
            byte route = logical.Length > 8 ? logical[8] : (byte)0;
            RouteControlFrame?.Invoke(this, new HidRouteControlFrameEventArgs(logical[4], status, route, logical));
        }
        // 其余 F5 帧非本通道职责（命令语义归 HidCommandChannel/FSM），只计数不外抛
    }

    /// <summary>只退订，不释放 <see cref="IHidTransport"/>（共享单例由容器统一释放）。</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _transport.ReportReceived -= OnReportReceived;
    }
}
