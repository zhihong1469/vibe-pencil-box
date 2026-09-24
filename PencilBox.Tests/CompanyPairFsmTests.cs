using System.Text;
using PencilBox.Core.Communication;
using PencilBox.Core.Fsm;
using PencilBox.Core.Protocol.Company;

namespace PencilBox.Tests;

/// <summary>
/// 配对会话状态机测试：**假通道 + 假时钟**，完全不依赖真设备（`04 §7` 验证策略）。
///
/// 覆盖：工厂三段（读笔端 → 写基站 → 回读校验）、`0x52` 三分支（01→02 / 02 / 03）、
/// 超时收敛（不悬挂）、缺通道拒绝、回读不一致不静默通过。
/// </summary>
public class CompanyPairFsmTests
{
    private static readonly byte[] Mac = { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };
    private static readonly byte[] OtherMac = { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };
    private const string BtName = "SunVoteR1:AABBCCDDEEFF";

    private static CompanyConfig Config(int maxRetry = 3, int pairingWindowMs = 60_000) =>
        new() { MaxRetry = maxRetry, PairingWindowMs = pairingWindowMs, ResponseTimeoutMs = 500 };

    private static CompanyPairFsm CreateFsm(out FakeClock clock, CompanyConfig? config = null,
        Func<int, bool>? autoComplete = null)
    {
        clock = new FakeClock(autoComplete);
        return new CompanyPairFsm(new CompanyCodec(), clock, config ?? Config());
    }

    // ================= 工厂快配对（串口读 → USB 写 → 回读） =================

    [Fact]
    public async Task FactoryPair_Success_UsesBothChannelsAndVerifies()
    {
        var pen = new FakeChannel(ChannelKind.Serial, _ => Resp(CompanyCmd.PairInfo, PairReadOkPayload(0x00)));
        var bs = new FakeChannel(ChannelKind.Hid, req => req[7] == CompanyParam.PairWrite
            ? Resp(CompanyCmd.PairInfo, CompanyParam.PairWrite, CompanyStatus.Ok, 0x00)
            : Resp(CompanyCmd.PairInfo, PairReadOkPayload(0x00)));

        var fsm = CreateFsm(out _);
        var result = await fsm.FactoryPairAsync(pen, bs, 0x00);

        Assert.True(result.Success, result.Message);
        Assert.Equal(PairFsmState.Success, fsm.State);

        // ① 串口读笔端：0x51 读请求（权威 LEN=0x05）
        Assert.Equal("F5AAAA056101510100", Convert.ToHexString(pen.Sent.Single()));

        // ② USB 写基站：权威 LEN=0x2B，MAC 一律显示序（PC 零转换 —— R9）
        Assert.Equal(2, bs.Sent.Count);
        Assert.Equal(0x2B, bs.Sent[0][3]);
        Assert.NotEqual(0x2C, bs.Sent[0][3]);
        Assert.Equal(Mac, bs.Sent[0][9..15]);

        // ③ USB 回读
        Assert.Equal("F5AAAA056101510100", Convert.ToHexString(bs.Sent[1]));
    }

    [Fact]
    public async Task FactoryPair_FailsWhenReadbackDiffers()
    {
        var pen = new FakeChannel(ChannelKind.Serial, _ => Resp(CompanyCmd.PairInfo, PairReadOkPayload(0x00)));
        var bs = new FakeChannel(ChannelKind.Hid, req => req[7] == CompanyParam.PairWrite
            ? Resp(CompanyCmd.PairInfo, CompanyParam.PairWrite, CompanyStatus.Ok, 0x00)
            : Resp(CompanyCmd.PairInfo, PairReadOkPayload(0x00, OtherMac)));

        var fsm = CreateFsm(out _);
        var result = await fsm.FactoryPairAsync(pen, bs, 0x00);

        Assert.False(result.Success);
        Assert.Equal(PairFsmState.Failed, fsm.State);
        Assert.Contains("不一致", result.Message);
    }

    [Fact]
    public async Task FactoryPair_FailsWhenWriteStatusNotOk()
    {
        var pen = new FakeChannel(ChannelKind.Serial, _ => Resp(CompanyCmd.PairInfo, PairReadOkPayload(0x00)));
        var bs = new FakeChannel(ChannelKind.Hid, _ =>
            Resp(CompanyCmd.PairInfo, CompanyParam.PairWrite, CompanyStatus.ParamError, 0x00));

        var result = await CreateFsm(out _).FactoryPairAsync(pen, bs, 0x00);

        Assert.False(result.Success);
        Assert.Contains("参数错", result.Message);
    }

    [Fact]
    public async Task FactoryPair_FailsWhenPenStatusIsFail()
    {
        var pen = new FakeChannel(ChannelKind.Serial, _ => Resp(CompanyCmd.PairInfo, PairReadFailPayload()));
        var bs = new FakeChannel(ChannelKind.Hid, _ => null);

        var result = await CreateFsm(out _).FactoryPairAsync(pen, bs, 0x00);

        Assert.False(result.Success);
        Assert.Contains("FF", result.Message);
        Assert.Empty(bs.Sent);   // 读笔失败即止，不写基站
    }

    [Fact]
    public async Task FactoryPair_RefusesWhenAChannelIsClosed()
    {
        var pen = new FakeChannel(ChannelKind.Serial, _ => null) { IsOpen = false };
        var bs = new FakeChannel(ChannelKind.Hid, _ => null);

        var result = await CreateFsm(out _).FactoryPairAsync(pen, bs, 0x00);

        Assert.False(result.Success);
        Assert.Contains("同时", result.Message);
        Assert.Empty(pen.Sent);
    }

    // ================= 用户自主配对（0x52） =================

    [Fact]
    public async Task EnterPairing_01Then02_SucceedsWithoutResending()
    {
        var usb = new FakeChannel(ChannelKind.Hid, _ => Resp(CompanyCmd.PairEnter, PairEnterStatus.Entered));
        var fsm = CreateFsm(out _);

        var states = new List<PairFsmState>();
        fsm.Transitioned += (_, t) => states.Add(t.To);

        var task = fsm.EnterPairingAsync(usb);

        // 设备先回 01 → 保持等待态、不重复发
        await WaitForStateAsync(fsm, PairFsmState.WaitingResult);
        Assert.Single(usb.Sent);

        usb.Push(Resp(CompanyCmd.PairEnter, EnterSuccessPayload()));

        var result = await task;

        Assert.True(result.Success, result.Message);
        Assert.Equal(Mac, result.Paired!.MacDisplay6);
        Assert.Equal(new[] { PairFsmState.EnteringPairing, PairFsmState.WaitingResult, PairFsmState.Success }, states);
    }

    [Fact]
    public async Task EnterPairing_Direct02_Succeeds()
    {
        var usb = new FakeChannel(ChannelKind.Hid, _ =>
            Resp(CompanyCmd.PairEnter, EnterSuccessPayload()));

        var fsm = CreateFsm(out _);
        var result = await fsm.EnterPairingAsync(usb);

        Assert.True(result.Success, result.Message);
        Assert.Equal(PairFsmState.Success, fsm.State);
        Assert.Single(usb.Sent);   // 未进入等待态，无额外帧
    }

    [Fact]
    public async Task EnterPairing_03_Fails()
    {
        var usb = new FakeChannel(ChannelKind.Hid, _ => Resp(CompanyCmd.PairEnter, PairEnterStatus.Failed));

        var result = await CreateFsm(out _).EnterPairingAsync(usb);

        Assert.False(result.Success);
        Assert.Contains("03", result.Message);
    }

    [Fact]
    public async Task EnterPairing_NoResponse_TimesOutWithoutHanging()
    {
        var usb = new FakeChannel(ChannelKind.Hid, _ => null);
        var fsm = CreateFsm(out var clock, Config(maxRetry: 1), autoComplete: _ => true);

        var result = await fsm.EnterPairingAsync(usb);

        Assert.False(result.Success);
        Assert.Equal(PairFsmState.Failed, fsm.State);
        Assert.Equal(2, usb.Sent.Count);                    // 首次 + MaxRetry(1)：不悬挂
        Assert.Contains("未响应", result.Message);
        Assert.All(clock.RequestedDelays, d => Assert.Equal(500, d));   // 超时来源＝配置（骨架 §5）
    }

    [Fact]
    public async Task EnterPairing_01ThenSilence_TimesOut()
    {
        var usb = new FakeChannel(ChannelKind.Hid, _ => Resp(CompanyCmd.PairEnter, PairEnterStatus.Entered));
        var fsm = CreateFsm(out var clock, Config(pairingWindowMs: 60_000), autoComplete: ms => ms >= 60_000);

        var result = await fsm.EnterPairingAsync(usb);

        Assert.False(result.Success);
        Assert.Contains("超时", result.Message);
        Assert.Contains(60_000, clock.RequestedDelays);     // 等待窗口＝配置
        Assert.Single(usb.Sent);                            // 进入等待后不重发 0x52
    }

    [Fact]
    public async Task EnterPairing_RefusesWhenChannelClosed()
    {
        var usb = new FakeChannel(ChannelKind.Hid, _ => null) { IsOpen = false };

        var result = await CreateFsm(out _).EnterPairingAsync(usb);

        Assert.False(result.Success);
        Assert.Contains("通道未打开", result.Message);
    }

    // ================= 黄金应答/负载组装 =================

    private static byte[] Resp(byte cmdType, params byte[] data) =>
        CompanyFrame.Build(CompanyFrame.BaseCmdResponse, cmdType, data);

    /// <summary>`0x51` 读应答负载：`PARAM(01) STATUS(00) NO MAC6 NAME32`</summary>
    private static byte[] PairReadOkPayload(byte no, byte[]? mac = null, string name = BtName)
    {
        var data = new List<byte> { CompanyParam.PairRead, CompanyStatus.Ok, no };
        data.AddRange(mac ?? Mac);
        data.AddRange(Name32(name));
        return data.ToArray();
    }

    /// <summary>`0x51` 读失败应答负载（结构完整、`STATUS=0xFF`；骨架 §6.1：读应答 LEN=0x2C）</summary>
    private static byte[] PairReadFailPayload()
    {
        var data = new List<byte> { CompanyParam.PairRead, CompanyStatus.Fail, 0x00 };
        data.AddRange(Mac);
        data.AddRange(Name32(BtName));
        return data.ToArray();
    }

    /// <summary>`0x52` 答 02 负载：`STATUS(02) MAC6 NAME32`</summary>
    private static byte[] EnterSuccessPayload() =>
        new List<byte> { PairEnterStatus.Success }.Concat(Mac).Concat(Name32(BtName)).ToArray();

    /// <summary>轮询等待 FSM 进入指定状态（异步续体可能不在测试线程上执行）</summary>
    private static async Task WaitForStateAsync(CompanyPairFsm fsm, PairFsmState state, int timeoutMs = 2000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (fsm.State != state && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(5);

        Assert.Equal(state, fsm.State);
    }

    private static byte[] Name32(string name)
    {
        var buffer = new byte[CompanyCodec.BtNameLength];
        var bytes = Encoding.ASCII.GetBytes(name);
        Array.Copy(bytes, buffer, Math.Min(bytes.Length, buffer.Length));
        return buffer;
    }

    // ================= 测试替身 =================

    /// <summary>假命令通道：记录下行帧，并按剧本同步回一帧（可 null＝不应答）；支持 <see cref="Push"/> 模拟延时上报。</summary>
    private sealed class FakeChannel : ICommandChannel
    {
        private readonly Func<byte[], byte[]?> _responder;

        public FakeChannel(ChannelKind kind, Func<byte[], byte[]?> responder)
        {
            Kind = kind;
            _responder = responder;
        }

        public ChannelKind Kind { get; }
        public bool IsOpen { get; set; } = true;
        public string Describe => $"FakeChannel({Kind})";
        public List<byte[]> Sent { get; } = new();

        public event EventHandler<byte[]>? FrameReceived;

        public Task SendAsync(ReadOnlySpan<byte> logicalFrame, CancellationToken cancellationToken = default)
        {
            var frame = logicalFrame.ToArray();
            Sent.Add(frame);

            var response = _responder(frame);
            if (response != null)
                FrameReceived?.Invoke(this, response);

            return Task.CompletedTask;
        }

        /// <summary>模拟设备"延时主动上报"（如 `0x52` 的 `02/03`）</summary>
        public void Push(byte[] logicalFrame) => FrameReceived?.Invoke(this, logicalFrame);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// 假时钟：按 <paramref name="autoComplete"/> 判定"该次延时应立即完成"——
    /// 未命中的延时**永不完成**，从而让"应答"赢得 <c>Task.WhenAny</c> 竞速（单测确定性）；
    /// 命中的延时立即完成，用于构造"无应答 → 超时收敛"路径。
    /// </summary>
    private sealed class FakeClock : IClock
    {
        private readonly Func<int, bool> _autoComplete;

        public FakeClock(Func<int, bool>? autoComplete = null) => _autoComplete = autoComplete ?? (_ => false);

        public List<int> RequestedDelays { get; } = new();
        public DateTime UtcNow { get; private set; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public Task DelayAsync(int milliseconds, CancellationToken cancellationToken = default)
        {
            RequestedDelays.Add(milliseconds);

            if (!_autoComplete(milliseconds))
                return new TaskCompletionSource<bool>().Task;

            UtcNow = UtcNow.AddMilliseconds(milliseconds);
            return Task.CompletedTask;
        }
    }
}
