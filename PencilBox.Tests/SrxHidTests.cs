// <copyright file="SrxHidTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Ble.SoftReceiver;
using PencilBox.Core.Communication;
using PencilBox.Infrastructure.Hid;

namespace PencilBox.Tests;

/// <summary>
/// HID 直送 mSBC（08 号方案 M1）单测：假传输层喂 64B 报告，验证分类/计数缺口/路由对/解码/插静音链路。
/// 真帧用 <see cref="MsbcGoldenVectors"/> 金标准（bit-exact 已在 MsbcDecoderTests 锚定）。
/// </summary>
public class SrxHidTests
{
    /// <summary>可注入报告的假 HID 传输：Record 捕获下行报告，Emit 回放上行业务数据。</summary>
    private sealed class FakeHidTransport : IHidTransport
    {
        public List<byte[]> SentReports { get; } = new();
        public bool IsConnected { get; set; } = true;
        public bool IsNoReportIdDevice { get; set; } = true;
        public HidDeviceInfo? CurrentDevice { get; set; }

#pragma warning disable CS0067
        public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
#pragma warning restore CS0067
        public event EventHandler<HidReportReceivedEventArgs>? ReportReceived;

        public Task<IReadOnlyList<HidDeviceInfo>> EnumerateDevicesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<HidDeviceInfo>>(Array.Empty<HidDeviceInfo>());

        public Task<bool> ConnectAsync(string devicePath, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SendReportAsync(byte reportId, byte[] data, CancellationToken ct = default)
        {
            SentReports.Add((byte[])data.Clone());
            return Task.CompletedTask;
        }

        public Task SendFeatureReportAsync(byte reportId, byte[] data, CancellationToken ct = default)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        /// <summary>回放一条上行报告（模拟 HID 读线程产出，已剥 0x00 虚拟 RID 的纯帧形态）。</summary>
        public void Emit(byte[] data)
            => ReportReceived?.Invoke(this, new HidReportReceivedEventArgs(0, data));
    }

    /// <summary>构造 64B 音频报告：[0..59]=mSBC 60B 帧（其首字节 0x01 兼任分类标记）、[60..61]=u16 LE 计数。</summary>
    private static byte[] AudioReport(ushort counter, byte[] frame60)
    {
        var report = new byte[64];
        Array.Copy(frame60, 0, report, 0, HidAudioChannel.MsbcFrameLength);
        report[60] = (byte)(counter & 0xFF);
        report[61] = (byte)(counter >> 8);
        return report;
    }

    /// <summary>构造下行 F5 逻辑帧（补零到 64B）。</summary>
    private static byte[] F5Report(params byte[] logical)
    {
        var report = new byte[64];
        logical.CopyTo(report, 0);
        return report;
    }

    // === HidAudioChannel：分类与计数缺口 ===

    [Fact]
    public void Channel_AudioReport_ClassifiesAndTracksCounter()
    {
        var transport = new FakeHidTransport();
        using var channel = new HidAudioChannel(transport);
        var counters = new List<ushort>();
        channel.AudioReportReceived += (_, e) => counters.Add(e.Counter);

        transport.Emit(AudioReport(100, MsbcGoldenVectors.Frame1));
        transport.Emit(AudioReport(101, MsbcGoldenVectors.Frame1));
        transport.Emit(AudioReport(102, MsbcGoldenVectors.Frame1));

        Assert.Equal(new ushort[] { 100, 101, 102 }, counters);
        var stats = channel.Snapshot();
        Assert.Equal(3, stats.AudioReports);
        Assert.Equal(0, stats.F5Reports);
        Assert.Equal(0, stats.GapEvents);
        Assert.True(stats.HasBaseline);
        Assert.Equal((ushort)102, stats.LastCounter);
    }

    [Fact]
    public void Channel_CounterGap_RaisesEventWithMissingCount()
    {
        var transport = new FakeHidTransport();
        using var channel = new HidAudioChannel(transport);
        HidCounterGapEventArgs? gap = null;
        channel.CounterGap += (_, e) => gap = e;

        transport.Emit(AudioReport(100, MsbcGoldenVectors.Frame1));
        transport.Emit(AudioReport(101, MsbcGoldenVectors.Frame1));
        transport.Emit(AudioReport(105, MsbcGoldenVectors.Frame1));   // 102/103/104 丢失

        Assert.NotNull(gap);
        Assert.Equal((ushort)101, gap!.LastCounter);
        Assert.Equal((ushort)105, gap.CurrentCounter);
        Assert.Equal(3, gap.GapFrames);
        Assert.Equal(1, channel.Snapshot().GapEvents);
        Assert.Equal(3, channel.Snapshot().GapFrames);
    }

    [Fact]
    public void Channel_CounterWrapAround_NoFalseGap()
    {
        var transport = new FakeHidTransport();
        using var channel = new HidAudioChannel(transport);
        var gaps = new List<HidCounterGapEventArgs>();
        channel.CounterGap += (_, e) => gaps.Add(e);

        transport.Emit(AudioReport(65534, MsbcGoldenVectors.Frame1));
        transport.Emit(AudioReport(65535, MsbcGoldenVectors.Frame1));
        transport.Emit(AudioReport(0, MsbcGoldenVectors.Frame1));
        transport.Emit(AudioReport(1, MsbcGoldenVectors.Frame1));

        Assert.Empty(gaps);
        Assert.Equal(4, channel.Snapshot().AudioReports);
    }

    [Fact]
    public void Channel_F5Classification_RouteFrameRaised_OthersCounted()
    {
        var transport = new FakeHidTransport();
        using var channel = new HidAudioChannel(transport);
        var routeFrames = new List<HidRouteControlFrameEventArgs>();
        channel.RouteControlFrame += (_, e) => routeFrames.Add(e);

        // E1 OK 应答（route=1 回读）
        transport.Emit(F5Report(0xF5, 0xAA, 0xAA, 0x05, 0xE1, 0x01, 0x56, 0x00, 0x01));
        // E0 看门狗回退事件
        transport.Emit(F5Report(0xF5, 0xAA, 0xAA, 0x04, 0xE0, 0x01, 0x56, 0x01));
        // 非 0x56 族的 F5 帧（只计数不外抛）
        transport.Emit(F5Report(0xF5, 0xAA, 0xAA, 0x04, 0xE1, 0x01, 0x03, 0x00));
        // 非 F5 非音频 → 丢弃
        transport.Emit(F5Report(0x42, 0x01, 0x02));

        Assert.Equal(2, routeFrames.Count);
        Assert.Equal(0xE1, routeFrames[0].CommandType);
        Assert.Equal(0x00, routeFrames[0].Status);
        Assert.Equal(0x01, routeFrames[0].Route);
        Assert.Equal(0xE0, routeFrames[1].CommandType);
        Assert.Equal(0x01, routeFrames[1].Status);
        var stats = channel.Snapshot();
        Assert.Equal(3, stats.F5Reports);
        Assert.Equal(1, stats.DroppedReports);
    }

    [Fact]
    public void Channel_FirstReportIsBaseline_NoGapAtStart()
    {
        var transport = new FakeHidTransport();
        using var channel = new HidAudioChannel(transport);
        var gaps = new List<HidCounterGapEventArgs>();
        channel.CounterGap += (_, e) => gaps.Add(e);

        // 首报即基线：首个计数是 500 而非 0，不产生缺口（08 号 §3.4）
        transport.Emit(AudioReport(500, MsbcGoldenVectors.Frame1));

        Assert.Empty(gaps);
        Assert.True(channel.HasBaseline);
    }

    // === SrxHidSession：路由对 + 解码 + 插静音 ===

    [Fact]
    public async Task Session_RouteOn_SendsFrame_AndCompletesOnE1Ok()
    {
        var transport = new FakeHidTransport();
        using var session = new SrxHidSession(transport);

        var routeTask = session.RouteToHidAsync(timeoutMs: 2000);
        // 给发送一点时间落账，再回放 E1 OK 应答
        await Task.Delay(50);
        var sent = Assert.Single(transport.SentReports);
        Assert.Equal(64, sent.Length);
        Assert.Equal(new byte[] { 0xF5, 0xAA, 0xAA, 0x04, 0x61, 0x01, 0x56, 0x01 }, sent[..8]);
        Assert.All(sent[8..], b => Assert.Equal(0, b));   // 补零到 64B 定长

        transport.Emit(F5Report(0xF5, 0xAA, 0xAA, 0x05, 0xE1, 0x01, 0x56, 0x00, 0x01));
        Assert.True(await routeTask);
        Assert.True(session.IsRoutedHid);
    }

    [Fact]
    public async Task Session_RouteOn_Timeout_ReturnsFalse()
    {
        var transport = new FakeHidTransport();
        using var session = new SrxHidSession(transport);

        Assert.False(await session.RouteToHidAsync(timeoutMs: 100));
        Assert.False(session.IsRoutedHid);
    }

    [Fact]
    public async Task Session_RouteOn_StatusFF_NoCapability_ReturnsFalse()
    {
        var transport = new FakeHidTransport();
        using var session = new SrxHidSession(transport);
        var logs = new List<string>();

        using var session2 = new SrxHidSession(transport, logs.Add);
        var routeTask = session2.RouteToHidAsync(timeoutMs: 2000);
        await Task.Delay(50);
        transport.Emit(F5Report(0xF5, 0xAA, 0xAA, 0x05, 0xE1, 0x01, 0x56, 0xFF, 0x01));

        Assert.False(await routeTask);
        Assert.Contains(logs, l => l.Contains("0xFF"));
    }

    [Fact]
    public async Task Session_RouteOff_SendsRouteZero()
    {
        var transport = new FakeHidTransport();
        using var session = new SrxHidSession(transport);

        var routeTask = session.RouteToUacAsync(timeoutMs: 2000);
        await Task.Delay(50);
        transport.Emit(F5Report(0xF5, 0xAA, 0xAA, 0x05, 0xE1, 0x01, 0x56, 0x00, 0x00));

        Assert.True(await routeTask);
        Assert.False(session.IsRoutedHid);
        Assert.Equal(0x00, transport.SentReports[0][7]);
    }

    [Fact]
    public async Task Session_WatchdogE0_RaisesFallbackAndUnsetsRoute()
    {
        var transport = new FakeHidTransport();
        using var session = new SrxHidSession(transport);

        var routeTask = session.RouteToHidAsync(timeoutMs: 2000);
        await Task.Delay(50);
        transport.Emit(F5Report(0xF5, 0xAA, 0xAA, 0x05, 0xE1, 0x01, 0x56, 0x00, 0x01));
        Assert.True(await routeTask);

        var fallback = 0L;
        session.WatchdogFallback += (_, _) => Interlocked.Increment(ref fallback);
        transport.Emit(F5Report(0xF5, 0xAA, 0xAA, 0x04, 0xE0, 0x01, 0x56, 0x01));

        Assert.Equal(1, Interlocked.Read(ref fallback));
        Assert.False(session.IsRoutedHid);
    }

    [Fact]
    public async Task Session_AudioReports_DecodeToPcmFrames()
    {
        var transport = new FakeHidTransport();
        using var session = new SrxHidSession(transport);
        var frames = new List<SrxHidAudioFrame>();
        session.AudioFrameReady += (_, f) => frames.Add(f);

        transport.Emit(AudioReport(10, MsbcGoldenVectors.Frame1));
        transport.Emit(AudioReport(11, MsbcGoldenVectors.Frame2));
        transport.Emit(AudioReport(12, MsbcGoldenVectors.Frame3));

        Assert.Equal(3, frames.Count);
        Assert.All(frames, f => Assert.False(f.IsSynthetic));
        Assert.All(frames, f => Assert.Equal(MsbcDecoder.PcmBytesPerFrame, f.Pcm.Length));
        // 金标准帧解码非全零（bit-exact 已在 MsbcDecoderTests 锚定；此处防"解码静默退化"）
        Assert.Contains(frames, f => f.Pcm.Any(b => b != 0));
        Assert.Equal(0, session.ChannelStats.GapEvents);
    }

    [Fact]
    public async Task Session_CounterGap_InsertsSyntheticSilence()
    {
        var transport = new FakeHidTransport();
        using var session = new SrxHidSession(transport);
        var frames = new List<SrxHidAudioFrame>();
        session.AudioFrameReady += (_, f) => frames.Add(f);

        transport.Emit(AudioReport(100, MsbcGoldenVectors.Frame1));
        transport.Emit(AudioReport(104, MsbcGoldenVectors.Frame1));   // 101~103 丢失

        // 2 真帧（100、104 都解码）+ 3 合成静音帧（节拍保连续；缺口帧在 104 真帧之前插入）
        Assert.Equal(5, frames.Count);
        Assert.False(frames[0].IsSynthetic);
        Assert.All(frames.Skip(1).Take(3), f => Assert.True(f.IsSynthetic));
        Assert.All(frames.Skip(1).Take(3), f => Assert.All(f.Pcm, b => Assert.Equal(0, b)));
        Assert.False(frames[4].IsSynthetic);
        Assert.Equal(3, session.ChannelStats.GapFrames);
    }

    [Fact]
    public async Task Session_BadPayload_CountedAsError_NoAudioFrame()
    {
        var transport = new FakeHidTransport();
        using var session = new SrxHidSession(transport);
        var frames = new List<SrxHidAudioFrame>();
        session.AudioFrameReady += (_, f) => frames.Add(f);

        // 包装标记对（[0]=0x01 [59]=0x00）但 SBC 同步字错（[2]≠0xAD）→ BadSyncword
        var bad = new byte[60];
        bad[0] = 0x01;
        bad[59] = 0x00;
        transport.Emit(AudioReport(200, bad));

        Assert.Empty(frames);
        var summary = session.Summary();
        Assert.Contains("同步错 1", summary);
    }

    [Fact]
    public async Task Session_RouteWithoutConnection_Throws()
    {
        var transport = new FakeHidTransport { IsConnected = false };
        using var session = new SrxHidSession(transport);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RouteToHidAsync());
    }

    [Fact]
    public async Task Session_Non56FamilyF5Frames_IgnoredBySession()
    {
        var transport = new FakeHidTransport();
        using var session = new SrxHidSession(transport);
        var frames = new List<SrxHidAudioFrame>();
        session.AudioFrameReady += (_, f) => frames.Add(f);

        // 其它 F5 命令应答（如 0x03 identify）不该触发路由/音频语义
        var routeTask = session.RouteToHidAsync(timeoutMs: 150);
        await Task.Delay(50);
        transport.Emit(F5Report(0xF5, 0xAA, 0xAA, 0x05, 0xE1, 0x01, 0x03, 0x00, 0x00));

        Assert.False(await routeTask);   // 超时（0x56 应答没来）
        Assert.Empty(frames);
    }
}
