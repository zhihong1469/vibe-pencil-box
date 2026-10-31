// <copyright file="SoftReceiverSession.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Communication;

namespace PencilBox.Core.Ble.SoftReceiver;

/// <summary>解码出的一帧音频（240B PCM s16LE 16kHz 单声道 = 7.5ms）。</summary>
public sealed record SoftReceiverAudioFrame(byte[] Pcm, byte AudioSeq, long LocalTimestampMs);

/// <summary>编码按键事件（0x11/0x12，软件接收器形态）。</summary>
public sealed record SoftReceiverKeyEvent(byte Page, byte KeyCode, byte Modifier, bool IsDown, long LocalTimestampMs);

/// <summary>档位切换事件（0x16，收到即报，无防重）。</summary>
public sealed record SoftReceiverSwitchEvent(byte Mode, long LocalTimestampMs);

/// <summary>状态槽变化（对齐基站收集逻辑：last[id]≠val 才报，未知 id 同样跟踪）。</summary>
public sealed record SoftReceiverSlotEvent(byte Id, byte Value);

/// <summary>RSP 应答（0x40 设备信息 / 0x41 电量等）。</summary>
public sealed record SoftReceiverRspEvent(byte CmdId, byte Status, byte[] Data);

/// <summary>会话统计（CLI 状态行/收尾汇总直接消费）。</summary>
public sealed class SoftReceiverStats
{
    public long DataFrames { get; internal set; }
    public long AudioDataFrames { get; internal set; }
    public long AudioFramesDecoded { get; internal set; }
    public long CrcErrors { get; internal set; }
    public long SyncErrors { get; internal set; }
    public long ParseErrors { get; internal set; }
    public long SeqJumps { get; internal set; }
    /// <summary>seq 跳变累计缺失音频帧数（丢包量的帧数口径）</summary>
    public long SeqGapFrames { get; internal set; }
    public byte? LastBattery { get; internal set; }
    public byte? LastDevState { get; internal set; }
    public byte? LastChargeState { get; internal set; }
    public bool KeyboardMode { get; internal set; }
    public bool MouseMode { get; internal set; }

    /// <summary>人类可读汇总行（CLI 收尾打印）。</summary>
    public string Summary() =>
        $"DATA帧={DataFrames} 音频DATA={AudioDataFrames} 解码={AudioFramesDecoded} " +
        $"CRC错={CrcErrors} 同步错={SyncErrors} 解析错={ParseErrors} seq跳={SeqJumps}(缺{SeqGapFrames}帧) " +
        $"电量={LastBattery?.ToString() ?? "—"} 充电={LastChargeState?.ToString() ?? "—"} " +
        $"键={(KeyboardMode ? "有" : "无")}/鼠={(MouseMode ? "有" : "无")}";
}

/// <summary>
/// 软件接收器会话编排（形态③）：建连 → 服务发现（svr1）→ 订阅 DATA/RSP →
/// 拆帧喂解析器/解码器 → 事件上抛。只依赖 <see cref="IBleCentral"/>（可假件单测），
/// 真实 WinRT 实现由 CLI/Desktop 注入。
///
/// 职责边界：本类不做 WAV 落盘、不做渲染、不做 CLI 文案——各消费端订阅事件自理。
/// 通知回调在 WinRT 线程池触发，事件处理器应快速返回（重活自行入队）。
/// </summary>
public sealed class SoftReceiverSession
{
    private readonly IBleCentral _central;
    private readonly Action<string>? _log;

    private readonly MsbcDecoder _decoder = new();
    private readonly byte[] _slotLast = new byte[16];   // 状态槽防抖缓存（对齐基站 0xFF 哨兵）
    private byte _lastAudioSeq = 0xFF;
    private bool _hasLastSeq;
    private CancellationTokenSource? _subscribeCts;
    private Task? _dataSubscribeTask;
    private Task? _rspSubscribeTask;

    /// <summary>统计（内部累加；消费端读快照即可）</summary>
    public SoftReceiverStats Stats { get; } = new();

    /// <summary>svr1 特征在位情况（发现结果；Data 为硬要求，Cmd/Rsp 缺失降级警告）</summary>
    public bool DataCharacteristicFound { get; private set; }
    public bool CmdCharacteristicFound { get; private set; }
    public bool RspCharacteristicFound { get; private set; }

    // ---- 事件 ----
    public event EventHandler<SoftReceiverAudioFrame>? AudioFrameReady;
    public event EventHandler<SoftReceiverKeyEvent>? KeyEvent;
    public event EventHandler<SoftReceiverSwitchEvent>? SwitchChanged;
    public event EventHandler<SoftReceiverSlotEvent>? SlotChanged;
    public event EventHandler<SoftReceiverRspEvent>? ResponseReceived;
    public event EventHandler<VpDataFrame>? DataFrameParsed;
    /// <summary>seq 跳变（参数=缺失帧数；低频事件，CLI 打点）</summary>
    public event EventHandler<long>? AudioSeqJump;
    /// <summary>人类可读诊断行（低频：连接/发现/跳变/坏帧样本）</summary>
    public event EventHandler<string>? Diagnostics;

    public SoftReceiverSession(IBleCentral central, Action<string>? log = null)
    {
        _central = central;
        _log = log;
    }

    public bool IsSubscribed => _dataSubscribeTask is { IsCompleted: false };

    /// <summary>
    /// 建连 + GATT 发现 + svr1 特征定位。失败抛 <see cref="InvalidOperationException"/>（带平台原文）。
    /// </summary>
    public async Task ConnectAsync(string macDisplay, CancellationToken ct = default)
    {
        var connect = await _central.ConnectAsync(new BleConnectRequest
        {
            MacDisplay = macDisplay,
            TimeoutMs = 10_000,
        }, ct);
        if (!connect.Ok)
            throw new InvalidOperationException($"连接失败：{connect.Error}");

        var discovery = await _central.DiscoverAsync(uncached: true, ct);
        if (!discovery.Ok)
            throw new InvalidOperationException($"服务发现失败：{discovery.Error}");

        bool Has(string uuid) => discovery.Services.Any(s =>
            s.Characteristics.Any(c => BleUuid.TryNormalize(uuid, out var n) && c.Uuid == n));

        DataCharacteristicFound = Has(VpProtocol.DataUuid);
        CmdCharacteristicFound = Has(VpProtocol.CmdUuid);
        RspCharacteristicFound = Has(VpProtocol.RspUuid);
        if (!DataCharacteristicFound)
            throw new InvalidOperationException(
                "未发现 svr1 DATA 特征（…0012）——对端不是 V1.2 笔端，或 GATT 表已缓存");

        _log?.Invoke($"svr1 特征：DATA={(DataCharacteristicFound ? "✓" : "✗")} " +
                     $"CMD={(CmdCharacteristicFound ? "✓" : "✗")} RSP={(RspCharacteristicFound ? "✓" : "✗")}");
    }

    /// <summary>
    /// 开始收流（订阅 DATA 必选 + RSP 可选），直至 <paramref name="ct"/> 取消。
    /// 返回=退订完成（统计可读）。未连接/无 DATA 特征抛异常。
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        if (!DataCharacteristicFound)
            throw new InvalidOperationException("未连接或未发现 DATA 特征（先 ConnectAsync）");

        ResetLinkState();

        _subscribeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var internalCt = _subscribeCts.Token;

        _dataSubscribeTask = _central.SubscribeContinuousAsync(VpProtocol.DataUuid, OnDataNotification, internalCt);
        if (RspCharacteristicFound)
            _rspSubscribeTask = _central.SubscribeContinuousAsync(
                VpProtocol.RspUuid, OnRspNotification, internalCt);

        Diagnostics?.Invoke(this, "收流中（DATA" + (RspCharacteristicFound ? "+RSP" : "") + "订阅）…");
        try
        {
            await _dataSubscribeTask.ConfigureAwait(false);
            if (_rspSubscribeTask != null)
                await _rspSubscribeTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常收尾（外部取消或 StopAsync）
        }
    }

    /// <summary>停止收流（退订；连接保持）。</summary>
    public async Task StopAsync()
    {
        if (_subscribeCts == null)
            return;
        _subscribeCts.Cancel();
        try { if (_dataSubscribeTask != null) await _dataSubscribeTask.ConfigureAwait(false); }
        catch (Exception ex) { _log?.Invoke($"DATA 退订异常：{ex.Message}"); }
        try { if (_rspSubscribeTask != null) await _rspSubscribeTask.ConfigureAwait(false); }
        catch (Exception ex) { _log?.Invoke($"RSP 退订异常：{ex.Message}"); }
        _subscribeCts.Dispose();
        _subscribeCts = null;
        _dataSubscribeTask = null;
        _rspSubscribeTask = null;
    }

    /// <summary>断开连接（收尾用）。</summary>
    public Task DisconnectAsync() => _central.DisconnectAsync();

    /// <summary>下发设置语音状态命令（0x20：0=关 1=识别 2=扩音）。无 CMD 特征时返回 false。</summary>
    public async Task<bool> SendVoiceStateAsync(byte state)
    {
        if (!CmdCharacteristicFound)
            return false;
        var cmd = VpCommandBuilder.SetVoiceState(state)
            ?? throw new InvalidOperationException("CMD 组装失败（长度异常）");
        var result = await _central.WriteAsync(VpProtocol.CmdUuid, cmd, withoutResponse: false);
        return result.Ok;
    }

    /// <summary>下发查询电量命令（0x41）；应答经 <see cref="ResponseReceived"/> 上抛。无 CMD 特征时返回 false。</summary>
    public async Task<bool> QueryBatteryAsync()
    {
        if (!CmdCharacteristicFound)
            return false;
        var cmd = VpCommandBuilder.QueryBatt()
            ?? throw new InvalidOperationException("CMD 组装失败（长度异常）");
        var result = await _central.WriteAsync(VpProtocol.CmdUuid, cmd, withoutResponse: false);
        return result.Ok;
    }

    // ---- 通知处理（WinRT 线程池上下文；保持轻量） ----

    private void OnDataNotification(byte[] blob)
    {
        Stats.DataFrames++;
        if (!VpFrameParser.TryParseData(blob, out var frame, out var err))
        {
            Stats.ParseErrors++;
            Diagnostics?.Invoke(this, $"DATA 解析失败：{err}（len={blob.Length}）");
            return;
        }
        if (frame == null)
            return;

        DataFrameParsed?.Invoke(this, frame);
        TrackSlots(frame);
        TrackSeq(frame);
        OnFrameEvents(frame);
        DecodeAudio(frame);
    }

    private void OnRspNotification(byte[] blob)
    {
        if (!VpFrameParser.TryParseRsp(blob, out var cmdId, out var status, out var data))
        {
            Stats.ParseErrors++;
            return;
        }
        ResponseReceived?.Invoke(this, new SoftReceiverRspEvent(cmdId, status, data.ToArray()));
    }

    private void TrackSlots(VpDataFrame frame)
    {
        Stats.LastChargeState = frame.ChargeState;
        foreach (var (id, value) in frame.StatusSlots)
        {
            if (id >= _slotLast.Length)
                continue;
            if (_slotLast[id] != value)
            {
                _slotLast[id] = value;
                if (id == VpProtocol.StateIdBatt)
                    Stats.LastBattery = value;
                else if (id == VpProtocol.StateIdDevState)
                    Stats.LastDevState = value;
                SlotChanged?.Invoke(this, new SoftReceiverSlotEvent(id, value));
            }
        }
    }

    private void TrackSeq(VpDataFrame frame)
    {
        if (frame.AudioFrameCount <= 0)
            return;
        Stats.AudioDataFrames++;
        if (_hasLastSeq && frame.AudioSeq != (byte)(_lastAudioSeq + 1))
        {
            long gap = (frame.AudioSeq - _lastAudioSeq - 1) & 0xFF;
            Stats.SeqJumps++;
            Stats.SeqGapFrames += gap;
            AudioSeqJump?.Invoke(this, gap);
            Diagnostics?.Invoke(this, $"音频 seq 跳变：{_lastAudioSeq}→{frame.AudioSeq}（缺 {gap} 帧）");
        }
        _lastAudioSeq = frame.AudioSeq;
        _hasLastSeq = true;
    }

    private void OnFrameEvents(VpDataFrame frame)
    {
        long ts = Environment.TickCount64;
        if (frame.CommonSlotValid)
        {
            var data = frame.EventData.Span;
            switch (frame.EventType)
            {
                case VpProtocol.EvtKeyDown:
                case VpProtocol.EvtKeyUp:
                    Stats.KeyboardMode = true;
                    KeyEvent?.Invoke(this, new SoftReceiverKeyEvent(
                        data.Length > 0 ? data[0] : (byte)0,
                        data.Length > 1 ? data[1] : (byte)0,
                        data.Length > 2 ? data[2] : (byte)0,
                        frame.EventType == VpProtocol.EvtKeyDown,
                        ts));
                    break;
                case VpProtocol.EvtSwitchChanged:
                    SwitchChanged?.Invoke(this, new SoftReceiverSwitchEvent(
                        data.Length > 0 ? data[0] : (byte)0, ts));
                    break;
            }
        }
        if (frame.MouseSlotValid)
            Stats.MouseMode = true;
    }

    private void DecodeAudio(VpDataFrame frame)
    {
        int n = frame.AudioFrameCount;
        if (n <= 0)
            return;
        var zone = frame.AudioZone.Span;
        var pcm = new byte[MsbcDecoder.PcmBytesPerFrame];
        for (int i = 0; i < n; i++)
        {
            var status = _decoder.DecodeWrapped(
                zone.Slice(i * VpProtocol.MsbcFrameLen, VpProtocol.MsbcFrameLen), pcm, out _);
            switch (status)
            {
                case MsbcDecodeStatus.Ok:
                    Stats.AudioFramesDecoded++;
                    AudioFrameReady?.Invoke(this,
                        new SoftReceiverAudioFrame((byte[])pcm.Clone(), frame.AudioSeq, Environment.TickCount64));
                    break;
                case MsbcDecodeStatus.CrcMismatch:
                    Stats.CrcErrors++;
                    break;
                case MsbcDecodeStatus.BadSyncword:
                    Stats.SyncErrors++;
                    break;
                default:
                    Stats.ParseErrors++;
                    break;
            }
        }
    }

    /// <summary>断连/重开清场：seq 哨兵 + 槽防抖 + 解码器状态（对齐基站 sdk_ble_vp_on_disconnect 语义）。</summary>
    private void ResetLinkState()
    {
        _lastAudioSeq = 0xFF;
        _hasLastSeq = false;
        Array.Fill(_slotLast, (byte)0xFF);
        _decoder.Reset();
    }
}
