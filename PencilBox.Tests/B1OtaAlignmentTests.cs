// <copyright file="B1OtaAlignmentTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Communication;
using PencilBox.Core.Fsm;
using PencilBox.Core.Protocol.Company;

namespace PencilBox.Tests;

/// <summary>
/// B1 透传轨 PC 适配口径测试（issues/08 PC-1~PC-6 / issues/05 §1-B/§1-D/§1-G 冻结口径）：
/// - **PC-2**：块等待超时锚点 = **本块首片发出**（发块耗时从窗口中扣除，非"末片发完"）；
/// - **PC-3**：超时**首选继续等待**；"重发开始帧"降为**一次性最后手段** → 仍耗尽 → Failed（不悬挂）；
/// - **PC-4**：同块反复重查**仅告警**（保险计数；失败唯一权威 = 设备终态 0x20 DCMD=02/03）；
/// - **PC-5**：`0x03` 无线轨读笔端版本显式填 target（G14：`0x10/0x11`，默认 `0x11`）；
/// - **PC-6**：`0x0A` 可先于开始应答被处理（隐式允许；迟到 DCMD=01 忽略）。
/// </summary>
public class B1OtaAlignmentTests
{
    private static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b);

    private static CompanyConfig Cfg() => new() { StartTimeoutMs = 500, BlockTimeoutMs = 500, ChunkGapMs = 5 };

    private static byte[] Image(int len)
    {
        var b = new byte[len];
        for (int i = 0; i < len; i++)
            b[i] = (byte)(i % 251);
        return b;
    }

    // ================= PC-5：0x03 target 黄金向量 =================

    [Fact]
    public void EncodeReadVersion_TargetPen_FillsG14Class()
    {
        var codec = new CompanyCodec();

        // 无线轨问笔端：target = 笔端类别（默认配置 0x11 = R1 V1.0；笔端按 0x1x 类别段应答）
        var pen = codec.EncodeReadVersion(new CompanyConfig().PenTargetClass);
        Assert.Equal("F5AAAA0461010311", Hex(pen));

        // 默认（串口轨/问基站）target=0x00：既有黄金向量不变
        Assert.Equal("F5AAAA0461010300", Hex(codec.EncodeReadVersion()));
    }

    // ================= PC-3：超时首选续等，重发开始帧=一次性最后手段 =================

    /// <summary>
    /// 设备：首发开始帧 → ALLOW + 拉块 0，此后**静默**（块 0 收到也不推进）；
    /// 最后手段重发开始帧 → 回 DCMD=02（同版本跳过）。
    /// 预期：先续等 OtaMaxRetry 轮（告警"继续等待"、不重发开始帧）→ 耗尽后一次性最后手段 → 设备 SKIP → Done。
    /// </summary>
    [Fact]
    public async Task Ota_BlockTimeout_WaitsBeforeLastResort()
    {
        var device = new SilentAfterBlock0Device();
        var warnings = new List<string>();
        var fsm = new CompanyOtaFsm(new CompanyCodec(), device, new StepClock(), Cfg());
        fsm.Warning += (_, w) => warnings.Add(w);

        var result = await fsm.RunAsync(Image(1024), Downtype.BaseStation, 0x01, 1, 0, 0);

        Assert.Equal(OtaOutcome.Skipped, result.Outcome);
        // 0x01 帧恰 2 帧：首发 + 最后手段（续等期间**不**重发开始帧）
        var startFrames = device.Sent.Where(f => f[7] == 0x01 && f[9] == OtaDcmd.Start).ToList();
        Assert.Equal(2, startFrames.Count);
        // 续等告警（首轮块等待 + 最后手段后再无块循环，故续等只发生在块 0 静默后）
        Assert.Contains(warnings, w => w.Contains("继续等待"));
        Assert.Contains(warnings, w => w.Contains("最后手段"));
        // 续等轮数 = OtaMaxRetry（告警文案 1/3、2/3、3/3）
        Assert.Equal(3, warnings.Count(w => w.Contains("继续等待")));
    }

    private sealed class SilentAfterBlock0Device : ICommandChannel
    {
        public ChannelKind Kind => ChannelKind.Hid;
        public bool IsOpen { get; set; } = true;
        public string Describe => "SilentAfterBlock0Device";
        public List<byte[]> Sent { get; } = new();
        public event EventHandler<byte[]>? FrameReceived;

        public Task SendAsync(ReadOnlySpan<byte> f, CancellationToken ct = default)
        {
            var frame = f.ToArray();
            Sent.Add(frame);
            if (frame[7] == 0x01 && frame[9] == OtaDcmd.Start)
            {
                if (Sent.Count(s => s[7] == 0x01 && s[9] == OtaDcmd.Start) == 1)
                {
                    Reply(new byte[] { 0x01, 0x00, OtaDcmd.Start });   // ALLOW
                    Reply(new byte[] { 0x0A, 0x00, 0x00, 0x00, 0x00 }); // 拉块 0 → 此后静默
                }
                else
                {
                    Reply(new byte[] { 0x01, 0x00, OtaDcmd.Skip });     // 最后手段重发 → 同版本跳过
                }
            }
            return Task.CompletedTask;
        }

        private void Reply(byte[] data) => FrameReceived?.Invoke(this,
            CompanyFrame.Build(CompanyFrame.BaseCmdResponse, CompanyCmd.Ota, data));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// 最后手段用尽后必须收敛（不悬挂，红线 5）：设备持续请求**越界**块（活设备 + 永无合法块请求）
    /// → 续等耗尽 → 最后手段一次 → 再耗尽 → Failed。0x01 帧恰 2 帧（不会无限重启会话）。
    /// </summary>
    [Fact]
    public async Task Ota_LastResort_Exhausted_FailsWithoutHanging()
    {
        var device = new OutOfRangeForeverDevice();
        var warnings = new List<string>();
        var fsm = new CompanyOtaFsm(new CompanyCodec(), device, new StepClock(), Cfg());
        fsm.Warning += (_, w) => warnings.Add(w);

        var result = await fsm.RunAsync(Image(1024), Downtype.BaseStation, 0x01, 1, 0, 0);

        Assert.Equal(OtaOutcome.Failed, result.Outcome);
        Assert.Equal(OtaFsmState.Failed, fsm.State);
        var startFrames = device.Sent.Where(f => f[7] == 0x01 && f[9] == OtaDcmd.Start).ToList();
        Assert.Equal(2, startFrames.Count);   // 首发 + 一次性最后手段
        Assert.Equal(6, warnings.Count(w => w.Contains("继续等待")));   // 两轮块等待 × 各 3 次续等
    }

    private sealed class OutOfRangeForeverDevice : ICommandChannel
    {
        public ChannelKind Kind => ChannelKind.Hid;
        public bool IsOpen { get; set; } = true;
        public string Describe => "OutOfRangeForeverDevice";
        public List<byte[]> Sent { get; } = new();
        public event EventHandler<byte[]>? FrameReceived;

        public Task SendAsync(ReadOnlySpan<byte> f, CancellationToken ct = default)
        {
            var frame = f.ToArray();
            Sent.Add(frame);
            if (frame[7] == 0x01 && frame[9] == OtaDcmd.Start)
            {
                Reply(new byte[] { 0x01, 0x00, OtaDcmd.Start });    // 每次都 ALLOW
                Reply(new byte[] { 0x0A, 0x00, 0x00, 0x7F, 0x00 }); // 但恒拉越界块 127（R5 忽略）
            }
            return Task.CompletedTask;
        }

        private void Reply(byte[] data) => FrameReceived?.Invoke(this,
            CompanyFrame.Build(CompanyFrame.BaseCmdResponse, CompanyCmd.Ota, data));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ================= PC-4：同块重查仅告警（保险计数） =================

    /// <summary>
    /// 设备反复重拉**同一块**（首拉 + 4 次重查 = 5 次 0x0A，超保险阈值 3）：
    /// FSM 每次整块重发（R1 不变），超阈值只告警**不 Failed**；终态由设备 0x20(02) 判定。
    /// </summary>
    [Fact]
    public async Task Ota_SameBlockRequery_OverLimit_OnlyWarns()
    {
        var device = new EndlessRequeryDevice();
        var warnings = new List<string>();
        var fsm = new CompanyOtaFsm(new CompanyCodec(), device, new StepClock(), Cfg());
        fsm.Warning += (_, w) => warnings.Add(w);

        var result = await fsm.RunAsync(Image(1024), Downtype.BaseStation, 0x01, 1, 0, 0);

        Assert.Equal(OtaOutcome.Succeeded, result.Outcome);              // 未因重查计数失败
        Assert.Contains(warnings, w => w.Contains("仅告警"));             // 超阈值告警（保险计数）
        var chunks = device.Sent.Count(f => f[7] == 0x07);
        Assert.Equal(22 * 5, chunks);                                    // 5 次拉块 → 每次整块 22 片
    }

    private sealed class EndlessRequeryDevice : ICommandChannel
    {
        private int _completedBlocks;
        public ChannelKind Kind => ChannelKind.Hid;
        public bool IsOpen { get; set; } = true;
        public string Describe => "EndlessRequeryDevice";
        public List<byte[]> Sent { get; } = new();
        public event EventHandler<byte[]>? FrameReceived;

        public Task SendAsync(ReadOnlySpan<byte> f, CancellationToken ct = default)
        {
            var frame = f.ToArray();
            Sent.Add(frame);
            if (frame.Length < 12 || frame[6] != CompanyCmd.Ota)
                return Task.CompletedTask;

            switch (frame[7])
            {
                case 0x01 when frame[9] == OtaDcmd.Start:
                    Reply(new byte[] { 0x01, 0x00, OtaDcmd.Start });
                    PullBlock0();
                    break;

                case 0x07 when frame[11] == 0x15:      // 块 0 的 22 片收齐
                    _completedBlocks++;
                    if (_completedBlocks < 5)
                        PullBlock0();                   // 同块重查（链路质量差模拟）
                    else
                        Reply(new byte[] { 0x20, 0x00, 0x02, 0x00 });   // 设备终态：完成
                    break;
            }
            return Task.CompletedTask;
        }

        private void PullBlock0() => Reply(new byte[] { 0x0A, 0x00, 0x00, 0x00, 0x00 });

        private void Reply(byte[] data) => FrameReceived?.Invoke(this,
            CompanyFrame.Build(CompanyFrame.BaseCmdResponse, CompanyCmd.Ota, data));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ================= PC-6：0x0A 先于开始应答被处理（隐式允许） =================

    /// <summary>
    /// B1 实测笔端出帧：`0x01(ALLOW)` → `0x0A(0)` 几乎同时（甚至 0x0A 先入事件队列）。
    /// FSM 须：`StartSent` 态收 `0x0A` → **隐式允许**（先入 AwaitingRequest 再发块）；
    /// 迟到的 `DCMD=01` 应答被忽略、不误报。端到端仍收敛。
    /// </summary>
    [Fact]
    public async Task Ota_0A_BeforeStartAck_ImplicitlyAllowed()
    {
        var device = new AckAfterPullDevice();
        var transitions = new List<string>();
        var fsm = new CompanyOtaFsm(new CompanyCodec(), device, new StepClock(), Cfg());
        fsm.Transitioned += (_, t) => transitions.Add(t.Reason);

        var result = await fsm.RunAsync(Image(1024), Downtype.BaseStation, 0x01, 1, 0, 0);

        Assert.Equal(OtaOutcome.Succeeded, result.Outcome);
        Assert.Contains(transitions, r => r.Contains("隐式允许"));       // PC-6 显式转移
    }

    private sealed class AckAfterPullDevice : ICommandChannel
    {
        public ChannelKind Kind => ChannelKind.Hid;
        public bool IsOpen { get; set; } = true;
        public string Describe => "AckAfterPullDevice";
        public List<byte[]> Sent { get; } = new();
        public event EventHandler<byte[]>? FrameReceived;

        public Task SendAsync(ReadOnlySpan<byte> f, CancellationToken ct = default)
        {
            var frame = f.ToArray();
            Sent.Add(frame);
            if (frame.Length < 12 || frame[6] != CompanyCmd.Ota)
                return Task.CompletedTask;

            switch (frame[7])
            {
                case 0x01 when frame[9] == OtaDcmd.Start:
                    Reply(new byte[] { 0x0A, 0x00, 0x00, 0x00, 0x00 });  // 先拉块（事件先入队）
                    Reply(new byte[] { 0x01, 0x00, OtaDcmd.Start });     // 后 ALLOW（迟到应答）
                    break;

                case 0x07 when frame[11] == 0x15:
                    Reply(new byte[] { 0x20, 0x00, 0x02, 0x00 });        // 单块镜像 → 完成
                    break;
            }
            return Task.CompletedTask;
        }

        private void Reply(byte[] data) => FrameReceived?.Invoke(this,
            CompanyFrame.Build(CompanyFrame.BaseCmdResponse, CompanyCmd.Ota, data));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ================= PC-2：块等待锚点 = 本块首片发出 =================

    /// <summary>
    /// 时钟随 DelayAsync 推进：块发送耗时（21 次片间 5ms = 105ms）须从 500ms 块等待窗口中**扣除**
    /// （锚点=首片发出 ⇒ 发完进 AwaitingRequest 时剩余 = 500−105 = 395ms），
    /// 而非"末片发完后重新计全额 500ms"。
    /// </summary>
    [Fact]
    public async Task Ota_BlockDeadline_AnchorIsFirstChunk()
    {
        var image = Image(2048);                              // 2 块，FakeOtaDevice 全时序应答
        var device = new FakeOtaDeviceShim(image);
        var clock = new AdvancingClock();
        // StartTimeoutMs=300 与 BlockTimeoutMs=500 取不同值，避免断言混淆（开始等待 vs 块等待）
        var fsm = new CompanyOtaFsm(new CompanyCodec(), device, clock,
            new CompanyConfig { StartTimeoutMs = 300, BlockTimeoutMs = 500, ChunkGapMs = 5 });

        var result = await fsm.RunAsync(image, Downtype.BaseStation, 0x01, 1, 0, 0);

        Assert.Equal(OtaOutcome.Succeeded, result.Outcome);
        Assert.Contains(300, clock.Delays);                   // StartSent 等开始应答（全额，与块无关）
        // 锚点生效的直接证据：块发送耗时（21×5ms=105）从窗口扣除 ⇒ 发完块后等待剩余 = 500−105 = 395；
        // 若锚点错成"末片发完"，块后等待将是全额 500（395 不会出现）
        Assert.Contains(395, clock.Delays);
        Assert.Equal(2, clock.Delays.Count(d => d == 395));   // 两块各扣一次（末块后直接终态，无等待）
    }

    /// <summary>
    /// 假 OTA 设备（与 <c>CompanyOtaTests.FakeOtaDevice</c> 同剧本：ALLOW → 拉块 → 收齐回进度并拉下一块 →
    /// 末块回 0x20(02)）——独立复制以避免跨测试类共享私有类型。
    /// </summary>
    private sealed class FakeOtaDeviceShim : ICommandChannel
    {
        private readonly byte[] _image;
        public ChannelKind Kind => ChannelKind.Hid;
        public bool IsOpen { get; set; } = true;
        public string Describe => "FakeOtaDeviceShim";

        public FakeOtaDeviceShim(byte[] image) => _image = image;

        public event EventHandler<byte[]>? FrameReceived;

        public Task SendAsync(ReadOnlySpan<byte> f, CancellationToken ct = default)
        {
            var frame = f.ToArray();
            if (frame.Length < 12 || frame[6] != CompanyCmd.Ota)
                return Task.CompletedTask;

            switch (frame[7])
            {
                case 0x01 when frame[9] == OtaDcmd.Start:
                    Reply(new byte[] { 0x01, frame[8], OtaDcmd.Start });
                    Reply(new byte[] { 0x0A, frame[8], 0x00, 0x00, 0x00 });
                    break;

                case 0x07 when frame[11] == 0x15:
                    int totalBlocks = (_image.Length + 1023) / 1024;
                    if (frame[10] + 1 >= totalBlocks)
                        Reply(new byte[] { 0x20, frame[8], 0x02, 0x00 });
                    else
                    {
                        Reply(new byte[] { 0x20, frame[8], 0x01, (byte)((frame[10] + 1) * 100 / totalBlocks) });
                        Reply(new byte[] { 0x0A, frame[8], 0x00, (byte)(frame[10] + 1), 0x00 });
                    }
                    break;
            }
            return Task.CompletedTask;
        }

        private void Reply(byte[] data) => FrameReceived?.Invoke(this,
            CompanyFrame.Build(CompanyFrame.BaseCmdResponse, CompanyCmd.Ota, data));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>延时即时完成但**推进 UtcNow** 的假时钟（验证锚点扣除发块耗时）</summary>
    private sealed class AdvancingClock : IClock
    {
        private DateTime _now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public DateTime UtcNow => _now;
        public List<int> Delays { get; } = new();

        public Task DelayAsync(int ms, CancellationToken ct = default)
        {
            Delays.Add(ms);
            _now = _now.AddMilliseconds(ms);
            return Task.CompletedTask;
        }
    }

    /// <summary>延时立即完成、UtcNow 恒定的假时钟（超时路径逻辑测试用）</summary>
    private sealed class StepClock : IClock
    {
        public DateTime UtcNow => new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public Task DelayAsync(int ms, CancellationToken ct = default) => Task.CompletedTask;
    }
}
