// <copyright file="SoftReceiverSessionTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Ble.SoftReceiver;
using PencilBox.Core.Communication;

namespace PencilBox.Tests;

/// <summary>
/// 软件接收器会话编排单测：假 Central 注入 DATA/RSP 通知，验证拆帧/解码/事件/统计链路
/// （对齐基站 sdk_ble_vp.c 消费语义）。
/// </summary>
public class SoftReceiverSessionTests
{
    /// <summary>可注入通知的假 Central：订阅后把排队的通知逐条回放。</summary>
    private sealed class FakeCentral : IBleCentral
    {
        public bool HasSvr1 { get; set; } = true;
        public Queue<byte[]> PendingData { get; } = new();
        public Queue<byte[]> PendingRsp { get; } = new();
        public List<byte[]> Written { get; } = new();

        public bool IsConnected { get; private set; }
        public string? ConnectedMacDisplay => IsConnected ? "C6:56:00:3B:AE:5D" : null;
#pragma warning disable CS0067
        public event EventHandler<bool>? ConnectionChanged;
#pragma warning restore CS0067

        public Task<BleConnectResult> ConnectAsync(BleConnectRequest request, CancellationToken ct = default)
        {
            IsConnected = true;
            return Task.FromResult(new BleConnectResult { Ok = true, MacDisplay = request.MacDisplay });
        }

        public Task DisconnectAsync()
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public Task<BleDiscoveryResult> DiscoverAsync(bool uncached = true, CancellationToken ct = default)
        {
            var chars = new List<BleGattCharacteristicInfo>();
            if (HasSvr1)
            {
                chars.Add(new BleGattCharacteristicInfo { ServiceUuid = VpProtocol.SvcUuid, Uuid = VpProtocol.DataUuid, Properties = 0x10 });
                chars.Add(new BleGattCharacteristicInfo { ServiceUuid = VpProtocol.SvcUuid, Uuid = VpProtocol.CmdUuid, Properties = 0x08 });
                chars.Add(new BleGattCharacteristicInfo { ServiceUuid = VpProtocol.SvcUuid, Uuid = VpProtocol.RspUuid, Properties = 0x10 });
            }
            return Task.FromResult(new BleDiscoveryResult
            {
                Ok = true,
                Services = new List<BleGattServiceInfo>
                {
                    new() { Uuid = VpProtocol.SvcUuid, Characteristics = chars }
                },
            });
        }

        public Task<BleValueResult> ReadAsync(string uuid, CancellationToken ct = default)
            => Task.FromResult(new BleValueResult { Ok = true });

        public Task<BleOpResult> WriteAsync(string uuid, byte[] data, bool withoutResponse = false,
            CancellationToken ct = default)
        {
            Written.Add(data);
            return Task.FromResult(new BleOpResult { Ok = true });
        }

        public Task<BleOpResult> SubscribeAsync(string uuid, TimeSpan duration, Action<string>? onHex,
            CancellationToken ct = default)
            => Task.FromResult(new BleOpResult { Ok = true });

        public Task<BleOpResult> SubscribeContinuousAsync(string uuid, Action<byte[]> onData,
            CancellationToken ct = default)
        {
            var queue = uuid == VpProtocol.DataUuid ? PendingData : PendingRsp;
            return Task.Run<BleOpResult>(async () =>
            {
                foreach (var blob in queue)
                {
                    ct.ThrowIfCancellationRequested();
                    onData(blob);
                    await Task.Delay(1, ct);
                }
                ct.ThrowIfCancellationRequested();
                // 收流持续到取消（与真实现同语义）：正常结束视为异常——测试用 CTS 取消收尾
                throw new OperationCanceledException(ct);
            }, ct);
        }

        public Task<BlePairResult> PairAsync(string protection = "encryption", CancellationToken ct = default)
            => Task.FromResult(new BlePairResult { Ok = true, StatusText = "Paired" });

        public Task<BlePairResult> UnpairAsync(CancellationToken ct = default)
            => Task.FromResult(new BlePairResult { Ok = true, StatusText = "Unpaired" });
    }

    private static byte[] BuildDataBlob(int n = 0, byte seq = 0, byte summaryExtra = 0,
        byte[]? commonSlot = null, byte[]? mouseSlot = null, byte batt = 0x55, byte charge = 0)
    {
        var blob = new byte[VpProtocol.AudioZoneOff + n * VpProtocol.MsbcFrameLen];
        blob[0] = (VpProtocol.VerV1_2 << 5) | VpProtocol.DataType;
        blob[1] = seq;
        blob[2] = (byte)(charge & 0x03);
        byte summary = (byte)(n & 0x03);
        if (commonSlot != null)
        {
            summary |= VpProtocol.SummaryCommonBit;
            commonSlot.CopyTo(blob, VpProtocol.CommonSlotOff);
        }
        if (mouseSlot != null)
        {
            summary |= VpProtocol.SummaryMouseBit;
            mouseSlot.CopyTo(blob, VpProtocol.MouseSlotOff);
        }
        blob[3] = summary;
        blob[12] = VpProtocol.StateIdDevState;
        blob[13] = 0x00;
        blob[14] = VpProtocol.StateIdBatt;
        blob[15] = batt;
        for (int i = 0; i < n; i++)
            MsbcGoldenVectors.Frame1.CopyTo(blob, VpProtocol.AudioZoneOff + i * VpProtocol.MsbcFrameLen);
        return blob;
    }

    /// <summary>跑会话直到给定通知全部消费完，返回（统计收集器）。</summary>
    private static async Task<(SoftReceiverSession Session, Collector C)> RunSessionAsync(FakeCentral central, params byte[][] notifications)
    {
        foreach (var n in notifications)
            central.PendingData.Enqueue(n);

        var session = new SoftReceiverSession(central);
        var collector = new Collector();
        collector.Hook(session);

        await session.ConnectAsync("C6:56:00:3B:AE:5D");

        using var cts = new CancellationTokenSource();
        var run = session.StartAsync(cts.Token);
        // 轮询等消费完（假件逐条回放，1ms/条），再取消收尾
        while (collector.DataFrames < notifications.Length)
            await Task.Delay(5);
        await Task.Delay(10);
        cts.Cancel();
        await session.StopAsync();
        try { await run; } catch (OperationCanceledException) { }
        await session.DisconnectAsync();
        return (session, collector);
    }

    private sealed class Collector
    {
        public long DataFrames;
        public List<SoftReceiverAudioFrame> Audio = new();
        public List<SoftReceiverKeyEvent> Keys = new();
        public List<SoftReceiverKeyActionEvent> KeyActions = new();
        public List<SoftReceiverMouseButtonEvent> MouseButtons = new();
        public List<SoftReceiverSwitchEvent> Switches = new();
        public List<SoftReceiverSlotEvent> Slots = new();
        public List<SoftReceiverRspEvent> Rsps = new();
        public List<long> SeqJumps = new();
        public List<string> Diag = new();

        public void Hook(SoftReceiverSession session)
        {
            session.DataFrameParsed += (_, _) => DataFrames++;
            session.AudioFrameReady += (_, f) => Audio.Add(f);
            session.KeyEvent += (_, k) => Keys.Add(k);
            session.KeyAction += (_, a) => KeyActions.Add(a);
            session.MouseButton += (_, m) => MouseButtons.Add(m);
            session.SwitchChanged += (_, s) => Switches.Add(s);
            session.SlotChanged += (_, s) => Slots.Add(s);
            session.ResponseReceived += (_, r) => Rsps.Add(r);
            session.AudioSeqJump += (_, g) => SeqJumps.Add(g);
            session.Diagnostics += (_, d) => Diag.Add(d);
        }
    }

    [Fact]
    public async Task Connect_FindsSvr1Characteristics()
    {
        var central = new FakeCentral();
        var session = new SoftReceiverSession(central);
        await session.ConnectAsync("C6:56:00:3B:AE:5D");
        Assert.True(session.DataCharacteristicFound);
        Assert.True(session.CmdCharacteristicFound);
        Assert.True(session.RspCharacteristicFound);
    }

    [Fact]
    public async Task Connect_MissingData_Throws()
    {
        var central = new FakeCentral { HasSvr1 = false };
        var session = new SoftReceiverSession(central);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ConnectAsync("C6:56:00:3B:AE:5D"));
    }

    [Fact]
    public async Task Receive_StatusOnlyFrame_PublishesSlotsAndBattery()
    {
        var central = new FakeCentral();
        var (session, collector) = await RunSessionAsync(central, BuildDataBlob(batt: 85, charge: 1));

        Assert.Equal(1, collector.DataFrames);
        Assert.Equal(1, collector.Slots.Count(s => s.Id == VpProtocol.StateIdBatt));
        Assert.Equal(85, collector.Slots.First(s => s.Id == VpProtocol.StateIdBatt).Value);
        Assert.Empty(collector.Audio);
        Assert.Equal((byte)85, session.Stats.LastBattery);
        Assert.Equal((byte)1, session.Stats.LastChargeState);
    }

    [Fact]
    public async Task Receive_AudioFrames_DecodedAndCounted()
    {
        var central = new FakeCentral();
        var (session, collector) = await RunSessionAsync(central,
            BuildDataBlob(n: 2, seq: 7),
            BuildDataBlob(n: 1, seq: 8));

        Assert.Equal(2, collector.DataFrames);
        Assert.Equal(3, collector.Audio.Count);
        Assert.All(collector.Audio, f => Assert.Equal(MsbcDecoder.PcmBytesPerFrame, f.Pcm.Length));
        // 首帧 PCM 与金标准一致（N=2 首块）
        Assert.Equal(MsbcGoldenVectors.ExpectedPcm1, collector.Audio[0].Pcm);
        Assert.Empty(collector.SeqJumps);
    }

    [Fact]
    public async Task Receive_SeqGap_CountedOnce()
    {
        var central = new FakeCentral();
        // seq 10 → 13：缺 2 帧
        var (session, collector) = await RunSessionAsync(central,
            BuildDataBlob(n: 1, seq: 10),
            BuildDataBlob(n: 1, seq: 13));

        Assert.Single(collector.SeqJumps);
        Assert.Equal(2, collector.SeqJumps[0]);
        Assert.Equal(1, session.Stats.SeqJumps);
        Assert.Equal(2, session.Stats.SeqGapFrames);
    }

    [Fact]
    public async Task Receive_KeyAndSwitchEvents()
    {
        var central = new FakeCentral();
        var keySlot = new byte[] { VpProtocol.EvtKeyDown, 0x03, 0x07, 0x04 };
        var switchSlot = new byte[] { VpProtocol.EvtSwitchChanged, 0x01, 0x02, 0x00 };
        var (session, collector) = await RunSessionAsync(central,
            BuildDataBlob(commonSlot: keySlot),
            BuildDataBlob(commonSlot: switchSlot));

        Assert.Single(collector.Keys);
        Assert.Equal((byte)0x07, collector.Keys[0].Page);
        Assert.Equal((byte)0x04, collector.Keys[0].KeyCode);
        Assert.True(collector.Keys[0].IsDown);
        Assert.Single(collector.Switches);
        Assert.Equal(0x02, collector.Switches[0].Mode);
    }

    [Fact]
    public async Task Receive_KeyActionAndMouseButtonEdges()
    {
        var central = new FakeCentral();
        // K5（VOL+）单击语义键 + 鼠标键沿序列：无→左(0x01)→左+右(0x03)（变化沿）→同值不重报
        var actionSlot = new byte[] { VpProtocol.EvtKeyAction, 0x02, 0x05, 0x01 };
        var (session, collector) = await RunSessionAsync(central,
            BuildDataBlob(commonSlot: actionSlot),
            BuildDataBlob(mouseSlot: new byte[] { 0x00, 0x00, 0x00, 0x00 }),
            BuildDataBlob(mouseSlot: new byte[] { 0x01, 0x00, 0x00, 0x00 }),
            BuildDataBlob(mouseSlot: new byte[] { 0x03, 0x00, 0x00, 0x00 }),   // 左保持 + 右按下 → 又一条沿
            BuildDataBlob(mouseSlot: new byte[] { 0x03, 0x00, 0x00, 0x00 }));  // 同值不重报

        var action = Assert.Single(collector.KeyActions);
        Assert.Equal((byte)5, action.KeyNum);
        Assert.Equal((byte)0x01, action.Action);
        Assert.Equal(1, session.Stats.KeyActions);

        Assert.Equal(2, collector.MouseButtons.Count);
        Assert.True(collector.MouseButtons[0].Left);
        Assert.False(collector.MouseButtons[0].Right);
        Assert.True(collector.MouseButtons[1].Right);
        Assert.Equal(2, session.Stats.MouseButtonEdges);
        Assert.True(session.Stats.MouseMode);
    }

    [Fact]
    public async Task Receive_SlotChangeDedup_SecondSameValueNotPublished()
    {
        var central = new FakeCentral();
        var (session, collector) = await RunSessionAsync(central,
            BuildDataBlob(batt: 85),
            BuildDataBlob(batt: 85),
            BuildDataBlob(batt: 84));

        Assert.Equal(2, collector.Slots.Count(s => s.Id == VpProtocol.StateIdBatt));
        Assert.Equal(85, collector.Slots.First(s => s.Id == VpProtocol.StateIdBatt).Value);
        Assert.Equal(84, collector.Slots.Last(s => s.Id == VpProtocol.StateIdBatt).Value);
    }

    [Fact]
    public async Task SendVoiceState_WritesCmdFrame()
    {
        var central = new FakeCentral();
        var session = new SoftReceiverSession(central);
        await session.ConnectAsync("C6:56:00:3B:AE:5D");
        Assert.True(await session.SendVoiceStateAsync(1));
        Assert.Equal(new byte[] { VpProtocol.CmdSetVoiceState, 0x01 }, central.Written[^1]);
    }
}
