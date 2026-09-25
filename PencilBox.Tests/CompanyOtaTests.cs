// <copyright file="CompanyOtaTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Communication;
using PencilBox.Core.Fsm;
using PencilBox.Core.Protocol.Company;

namespace PencilBox.Tests;

/// <summary>
/// `0x16` 升级族黄金向量（骨架 §6.1：开始 `LEN=0x10`、拉块 `0x08`、数据片 `0x38`、进度 `0x07`）
/// + 拉模式 FSM 离线测试（假通道 + 假时钟：正常 / 跳过 / 拒绝 / 重查 R1 / 越界 R5 / 超时 R2 / 退出 / E23）。
/// </summary>
public class CompanyOtaTests
{
    private static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b);

    // ================= Codec：黄金向量 =================

    [Fact]
    public void EncodeOtaSession_Start_IsGoldenVector()
    {
        var codec = new CompanyCodec();
        var frame = codec.EncodeOtaSession(Downtype.BaseStation, OtaDcmd.Start,
            hver: 0x01, sver1: 1, sver2: 0, sver3: 0, crc16: 0xABCD, fileLen: 2048);

        // F5 AA AA 10 | 61 01 16 | 01 00 01 01 | 01 00 00 | AB CD | 00 00 08 00
        Assert.Equal(20, frame.Length);
        Assert.Equal(0x10, frame[3]);                       // 权威 LEN（原件 0x11 属 +1 笔误族）
        Assert.NotEqual(0x11, frame[3]);
        Assert.Equal("F5AAAA10610116" + "010001" + "01" + "010000" + "ABCD" + "00000800", Hex(frame));

        Assert.True(codec.TryParseOtaSessionResp(
            new byte[] { 0x01, 0x00, OtaDcmd.Start }, out var dt, out var dcmd));
        Assert.Equal(0x00, dt);
        Assert.Equal(OtaDcmd.Start, dcmd);
    }

    [Fact]
    public void EncodeOtaChunk_IsGoldenVector()
    {
        var codec = new CompanyCodec();
        var data48 = new byte[48];
        for (int i = 0; i < 48; i++)
            data48[i] = (byte)i;

        var frame = codec.EncodeOtaChunk(Downtype.BaseStation, 0x00, 0x05, 0x15, data48);

        Assert.Equal(60, frame.Length);                     // 4 + LEN(56)
        Assert.Equal(0x38, frame[3]);
        Assert.Equal(0x07, frame[7]);                       // MODE=07
        Assert.Equal((byte)Downtype.BaseStation, frame[8]); // DOWNTYPE 回显会话目标（E23）
        Assert.Equal(0x00, frame[9]);                       // DOWNID
        Assert.Equal(0x05, frame[10]);                      // PACKH
        Assert.Equal(0x15, frame[11]);                      // PACKL
        Assert.Equal(data48, frame[12..60]);
    }

    [Fact]
    public void ParseOta0A_And_20_Golden()
    {
        var codec = new CompanyCodec();

        // 设备拉块：E1 01 16 0A 00 00 PACKH 00（DATA = [0A][DT][DOWNID][PACKH][PACKL]）
        Assert.True(codec.TryParseOtaBlockRequest(
            new byte[] { 0x0A, 0x00, 0x00, 0x03, 0x00 }, out var dt, out var downId, out var packH));
        Assert.Equal(0x00, dt);
        Assert.Equal(0x00, downId);
        Assert.Equal(0x03, packH);

        // 进度：E1 01 16 20 00 01 32（DATA = [20][DT][DCMD][percent]）
        Assert.True(codec.TryParseOtaProgress(
            new byte[] { 0x20, 0x00, 0x01, 0x32 }, out var pdt, out var pdcmd, out var percent));
        Assert.Equal(0x00, pdt);
        Assert.Equal(0x01, pdcmd);
        Assert.Equal(0x32, percent);                        // 50%
    }

    [Fact]
    public void Crc16_KnownVector()
    {
        Assert.Equal(0x29B1, Crc16CcittFalse.Compute("123456789"u8.ToArray()));
        Assert.Equal(0xFFFF, Crc16CcittFalse.Compute(Array.Empty<byte>()));
    }

    // ================= FSM：假通道 + 假时钟 =================

    /// <summary>
    /// 假 OTA 设备（0x16 全时序）：DCMD=01 → 回显并拉块 0；每收齐一块（22 片）→ 回进度并拉下一块；
    /// 末块后按收到字节比对镜像 → 回 `20/02` 完成（不一致回 `20/03` 原因 04）。对 `DCMD=00` 回显退出。
    /// </summary>
    private sealed class FakeOtaDevice : ICommandChannel
    {
        private readonly byte[] _image;
        private readonly byte[] _received;

        public FakeOtaDevice(byte[] image)
        {
            _image = image;
            _received = new byte[((image.Length + 1023) / 1024) * 1024];
        }

        public ChannelKind Kind => ChannelKind.Hid;
        public bool IsOpen { get; set; } = true;
        public string Describe => "FakeOtaDevice";
        public List<byte[]> Sent { get; } = new();
        public int BlocksPulled { get; private set; }

        /// <summary>模拟设备块级超时重查：收到当前块第 N 片（1 起）后重拉 <see cref="RequeryPackH"/>（一次性）</summary>
        public int RequeryAtChunkIndex { get; set; } = -1;
        public byte RequeryPackH { get; set; }
        private bool _requeryDone;

        public event EventHandler<byte[]>? FrameReceived;

        public Task SendAsync(ReadOnlySpan<byte> logicalFrame, CancellationToken cancellationToken = default)
        {
            var frame = logicalFrame.ToArray();
            Sent.Add(frame);

            if (frame.Length < 10 || frame[6] != CompanyCmd.Ota)
                return Task.CompletedTask;

            switch (frame[7])
            {
                case 0x01:   // 会话帧（frame[9] = DCMD）
                    Reply(Resp(0x01, frame[8], frame[9]));
                    if (frame[9] == OtaDcmd.Start)
                        PullBlock(0);
                    break;

                case 0x07:   // 数据片
                    var packH = frame[10];
                    var packL = frame[11];
                    var dst = _received.AsSpan((packH << 10) + packL * 48);
                    frame.AsSpan(12, 48)[..Math.Min(48, dst.Length)].CopyTo(dst);

                    if (RequeryAtChunkIndex >= 0 && !_requeryDone && packL + 1 == RequeryAtChunkIndex)
                    {
                        _requeryDone = true;
                        PullBlock(RequeryPackH);            // 第 N 片后设备超时重查（R1）
                        break;
                    }

                    if (packL == 0x15)                      // 该块 22 片齐
                    {
                        int totalBlocks = (_image.Length + 1023) / 1024;
                        if (packH + 1 >= totalBlocks)
                        {
                            var crcOk = _received.AsSpan(0, _image.Length).SequenceEqual(_image);
                            Reply(Resp(0x20, 0x00, crcOk ? (byte)0x02 : (byte)0x03, crcOk ? (byte)0 : (byte)0x04));
                        }
                        else
                        {
                            Reply(Resp(0x20, 0x00, 0x01, (byte)((packH + 1) * 100 / totalBlocks)));
                            PullBlock((byte)(packH + 1));
                        }
                    }
                    break;
            }

            return Task.CompletedTask;
        }

        /// <summary>模拟设备拉块 / 重查（R1 用）</summary>
        public void PullBlock(byte packH)
        {
            BlocksPulled++;
            Reply(Resp(0x0A, 0x00, 0x00, packH));
        }

        private void Reply(byte[] frame) => FrameReceived?.Invoke(this, frame);

        private static byte[] Resp(byte mode, byte dt, byte dcmd, byte packH = 0)
        {
            var data = mode switch
            {
                0x01 => new byte[] { 0x01, dt, dcmd },
                0x0A => new byte[] { 0x0A, dt, 0x00, packH, 0x00 },
                0x20 => new byte[] { 0x20, dt, dcmd, packH },
                _ => throw new ArgumentOutOfRangeException(nameof(mode))
            };
            return CompanyFrame.Build(CompanyFrame.BaseCmdResponse, CompanyCmd.Ota, data);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>延时立即完成的假时钟（构造超时路径；记录全部延时供断言节拍）</summary>
    private sealed class StepClock : IClock
    {
        public List<int> Delays { get; } = new();
        public DateTime UtcNow => new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public Task DelayAsync(int ms, CancellationToken ct = default)
        {
            Delays.Add(ms);
            return Task.CompletedTask;
        }
    }

    /// <summary>完全不应答的假设备（会话永不推进，用于"超时收敛、不悬挂"路径）——故不产生任何上行帧</summary>
    private sealed class NullDevice : ICommandChannel
    {
        public ChannelKind Kind => ChannelKind.Hid;
        public bool IsOpen { get; set; } = true;
        public string Describe => "NullDevice";
        public List<byte[]> Sent { get; } = new();
        public event EventHandler<byte[]>? FrameReceived { add { } remove { } }
        public Task SendAsync(ReadOnlySpan<byte> f, CancellationToken ct = default)
        {
            Sent.Add(f.ToArray());
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static CompanyConfig Cfg() => new() { StartTimeoutMs = 500, BlockTimeoutMs = 500, ChunkGapMs = 5 };

    private static byte[] Image(int len)
    {
        var b = new byte[len];
        for (int i = 0; i < len; i++)
            b[i] = (byte)(i % 251);
        return b;
    }

    [Fact]
    public async Task Ota_TwoBlocks_EndToEnd_Succeeds()
    {
        var image = Image(2048);
        var device = new FakeOtaDevice(image);
        var clock = new StepClock();
        var fsm = new CompanyOtaFsm(new CompanyCodec(), device, clock, Cfg());

        var result = await fsm.RunAsync(image, Downtype.BaseStation, 0x01, 1, 0, 0);

        Assert.Equal(OtaOutcome.Succeeded, result.Outcome);
        Assert.Equal(2, result.TotalBlocks);
        Assert.Equal(2, result.BlocksDone);
        Assert.Equal(OtaFsmState.Done, fsm.State);

        // 帧序：开始 1 + 片 22×2 = 45 帧；片 PACKL 0x00~0x15 递增、PACKH 分块
        var chunks = device.Sent.Where(f => f[7] == 0x07).ToList();
        Assert.Equal(44, chunks.Count);
        Assert.All(chunks, c => Assert.Equal((byte)Downtype.BaseStation, c[8]));   // E23：回显会话目标
        Assert.Equal(0x00, chunks[0][11]);
        Assert.Equal(0x15, chunks[21][11]);
        Assert.Equal(0x01, chunks[22][10]);

        // 片间 5ms 节拍：每块 21 次片间延时（末片不延时）
        Assert.Equal(2 * 21, clock.Delays.Count(d => d == 5));
    }

    [Fact]
    public async Task Ota_LastBlockTruncated_PadsAndStillSucceeds()
    {
        var image = Image(1500);                            // 2 块：末块仅 476B
        var device = new FakeOtaDevice(image);
        var fsm = new CompanyOtaFsm(new CompanyCodec(), device, new StepClock(), Cfg());

        var result = await fsm.RunAsync(image, Downtype.BaseStation, 0x01, 1, 0, 0);

        Assert.Equal(OtaOutcome.Succeeded, result.Outcome); // 末块补零不影响设备按 FILELEN 比对
        var lastChunk = device.Sent.Last(f => f[7] == 0x07);
        Assert.Equal(0x15, lastChunk[11]);                  // 末片仍 PACKL=0x15（补零到 48B）
    }

    [Fact]
    public async Task Ota_Skip_WhenDeviceReportsSameVersion()
    {
        var device = new FakeOtaDevice(Image(2048));
        // 开始应答强制改为 DCMD=02（跳过）且不拉块
        var skip = new SkipDevice();
        var fsm = new CompanyOtaFsm(new CompanyCodec(), skip, new StepClock(), Cfg());

        var result = await fsm.RunAsync(Image(2048), Downtype.BaseStation, 0x01, 1, 0, 0);

        Assert.Equal(OtaOutcome.Skipped, result.Outcome);
        Assert.Single(skip.Sent);                           // 只有开始帧，无任何数据片
    }

    private sealed class SkipDevice : ICommandChannel
    {
        public ChannelKind Kind => ChannelKind.Hid;
        public bool IsOpen { get; set; } = true;
        public string Describe => "SkipDevice";
        public List<byte[]> Sent { get; } = new();
        public event EventHandler<byte[]>? FrameReceived;

        public Task SendAsync(ReadOnlySpan<byte> f, CancellationToken ct = default)
        {
            var frame = f.ToArray();
            Sent.Add(frame);
            if (frame[7] == 0x01)
                FrameReceived?.Invoke(this, CompanyFrame.Build(CompanyFrame.BaseCmdResponse, CompanyCmd.Ota,
                    new byte[] { 0x01, 0x00, OtaDcmd.Skip }));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Ota_Reject_FailsImmediately()
    {
        var device = new RejectDevice();
        var fsm = new CompanyOtaFsm(new CompanyCodec(), device, new StepClock(), Cfg());

        var result = await fsm.RunAsync(Image(1024), Downtype.BaseStation, 0x01, 1, 0, 0);

        Assert.Equal(OtaOutcome.Failed, result.Outcome);
        Assert.Equal(OtaFsmState.Failed, fsm.State);
    }

    private sealed class RejectDevice : ICommandChannel
    {
        public ChannelKind Kind => ChannelKind.Hid;
        public bool IsOpen { get; set; } = true;
        public string Describe => "RejectDevice";
        public event EventHandler<byte[]>? FrameReceived;

        public Task SendAsync(ReadOnlySpan<byte> f, CancellationToken ct = default)
        {
            var frame = f.ToArray();
            if (frame[7] == 0x01)
                FrameReceived?.Invoke(this, CompanyFrame.Build(CompanyFrame.BaseCmdResponse, CompanyCmd.Ota,
                    new byte[] { 0x01, 0x00, OtaDcmd.Reject }));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Ota_Requery_R1_RestartsBlock()
    {
        var image = Image(1024);
        var device = new FakeOtaDevice(image)
        {
            RequeryAtChunkIndex = 5,                          // 收到第 5 片后设备重查块 0
            RequeryPackH = 0
        };
        var warnings = new List<string>();
        var fsm = new CompanyOtaFsm(new CompanyCodec(), device, new StepClock(), Cfg());
        fsm.Warning += (_, w) => warnings.Add(w);

        var result = await fsm.RunAsync(image, Downtype.BaseStation, 0x01, 1, 0, 0);

        Assert.Equal(OtaOutcome.Succeeded, result.Outcome); // 重查后仍收敛到完成
        Assert.Contains(warnings, w => w.Contains("R1"));   // 有重查告警
        var chunks = device.Sent.Count(f => f[7] == 0x07);
        Assert.Equal(5 + 22, chunks);                        // 前 5 片作废 + 整块重发 22 片
    }

    [Fact]
    public async Task Ota_OutOfRangePackH_IgnoredWithWarning_R5()
    {
        var device = new OutOfRangeDevice();
        var warnings = new List<string>();
        var fsm = new CompanyOtaFsm(new CompanyCodec(), device, new StepClock(), Cfg());
        fsm.Warning += (_, w) => warnings.Add(w);

        var result = await fsm.RunAsync(Image(1024), Downtype.BaseStation, 0x01, 1, 0, 0);

        Assert.Equal(OtaOutcome.Failed, result.Outcome);    // 忽略后无后续 → 块等待超时收敛
                                                           // （B1 新语义：续等 3 轮 → 一次性最后手段 → 再耗尽 → Failed）
        Assert.Contains(warnings, w => w.Contains("越界") && w.Contains("R5"));
        Assert.DoesNotContain(device.Sent, f => f[7] == 0x07);   // 不补零糊弄：没发任何片
        Assert.Equal(2, device.Sent.Count(f => f[7] == 0x01 && f[9] == OtaDcmd.Start));   // 首发 + 最后手段
    }

    private sealed class OutOfRangeDevice : ICommandChannel
    {
        public ChannelKind Kind => ChannelKind.Hid;
        public bool IsOpen { get; set; } = true;
        public string Describe => "OutOfRangeDevice";
        public List<byte[]> Sent { get; } = new();
        public event EventHandler<byte[]>? FrameReceived;

        public Task SendAsync(ReadOnlySpan<byte> f, CancellationToken ct = default)
        {
            var frame = f.ToArray();
            Sent.Add(frame);
            if (frame[7] == 0x01)
            {
                FrameReceived?.Invoke(this, CompanyFrame.Build(CompanyFrame.BaseCmdResponse, CompanyCmd.Ota,
                    new byte[] { 0x01, 0x00, OtaDcmd.Start }));
                FrameReceived?.Invoke(this, CompanyFrame.Build(CompanyFrame.BaseCmdResponse, CompanyCmd.Ota,
                    new byte[] { 0x0A, 0x00, 0x00, 0x7F, 0x00 }));   // PACKH=127 越界
            }
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Ota_WrongDowntype_IgnoredWithWarning_E23()
    {
        var device = new WrongDowntypeDevice();
        var warnings = new List<string>();
        var fsm = new CompanyOtaFsm(new CompanyCodec(), device, new StepClock(), Cfg());
        fsm.Warning += (_, w) => warnings.Add(w);

        var result = await fsm.RunAsync(Image(1024), Downtype.BaseStation, 0x01, 1, 0, 0);

        Assert.Equal(OtaOutcome.Failed, result.Outcome);
        Assert.Contains(warnings, w => w.Contains("E23"));
        Assert.DoesNotContain(device.Sent, f => f.Length > 8 && f[7] == 0x07);   // 不响应错误目标的拉块
    }

    private sealed class WrongDowntypeDevice : ICommandChannel
    {
        public ChannelKind Kind => ChannelKind.Hid;
        public bool IsOpen { get; set; } = true;
        public string Describe => "WrongDowntypeDevice";
        public List<byte[]> Sent { get; } = new();
        public event EventHandler<byte[]>? FrameReceived;

        public Task SendAsync(ReadOnlySpan<byte> f, CancellationToken ct = default)
        {
            var frame = f.ToArray();
            Sent.Add(frame);
            if (frame[7] == 0x01)
            {
                FrameReceived?.Invoke(this, CompanyFrame.Build(CompanyFrame.BaseCmdResponse, CompanyCmd.Ota,
                    new byte[] { 0x01, 0x00, OtaDcmd.Start }));
                FrameReceived?.Invoke(this, CompanyFrame.Build(CompanyFrame.BaseCmdResponse, CompanyCmd.Ota,
                    new byte[] { 0x0A, 0x01, 0x00, 0x00, 0x00 }));   // DOWNTYPE=0x01 ≠ 会话目标 0x00
            }
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Ota_AbortAfterBlock_ExitsWithEcho()
    {
        var image = Image(2048);
        var device = new FakeOtaDevice(image);              // 对 DCMD=00 回显（FakeOtaDevice 内置）
        var fsm = new CompanyOtaFsm(new CompanyCodec(), device, new StepClock(), Cfg());

        var result = await fsm.RunAsync(image, Downtype.BaseStation, 0x01, 1, 0, 0, abortAfterBlock: 1);

        Assert.Equal(OtaOutcome.Aborted, result.Outcome);
        Assert.Equal(OtaFsmState.Aborted, fsm.State);
        Assert.Equal(1, result.BlocksDone);                 // 只完成第 1 块
        Assert.Contains(device.Sent, f => f[7] == 0x01 && f[9] == OtaDcmd.Exit);   // 发过 DCMD=00
    }

    [Fact]
    public async Task Ota_NoResponseAtAll_TimesOutWithoutHanging()
    {
        var device = new NullDevice();
        var fsm = new CompanyOtaFsm(new CompanyCodec(), device, new StepClock(), Cfg());

        var result = await fsm.RunAsync(Image(1024), Downtype.BaseStation, 0x01, 1, 0, 0);

        Assert.Equal(OtaOutcome.Failed, result.Outcome);
        Assert.Equal(OtaFsmState.Failed, fsm.State);
        Assert.Equal(1 + 3, device.Sent.Count);             // 首发 + OtaMaxRetry(3) 重发，不悬挂
    }
}
