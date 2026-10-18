// <copyright file="KeymapApplyServiceTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Communication;
using PencilBox.Core.Fsm;
using PencilBox.Core.Models;
using PencilBox.Core.Protocol.Company;

namespace PencilBox.Tests;

/// <summary>
/// `0x55` 整表下发会话服务（pc-issues/05 §2/§3，pc-issues/05「PC 侧实现回执」）：
/// fake 基站通道下的全链事务（探测→逐条写→提交）、无应答降级、中途拒绝中止语义。
/// </summary>
public class KeymapApplyServiceTests
{
    /// <summary>假基站：对 0x55 各 PARAM 回 STATUS=00 应答（可注入元信息 STATUS / 条目拒绝码 / 静默）。</summary>
    private sealed class FakeKeymapDevice : ICommandChannel
    {
        public ChannelKind Kind => ChannelKind.Hid;
        public bool IsOpen { get; set; } = true;
        public string Describe => "FakeKeymapDevice";

        public byte MetaStatus { get; set; }
        public byte EntryStatus { get; set; }
        public bool Silent { get; set; }

        /// <summary>收到的请求 DATA（CMDTYPE 后的载荷），按序。</summary>
        public List<byte[]> Requests { get; } = [];

        public event EventHandler<byte[]>? FrameReceived;

        public Task SendAsync(ReadOnlySpan<byte> logicalFrame, CancellationToken cancellationToken = default)
        {
            var f = logicalFrame.ToArray();
            if (Silent || f.Length < 8 || f[6] != CompanyCmd.KeymapConfig)
                return Task.CompletedTask;

            Requests.Add(f[7..]);
            var param = f[7];
            var payload = param switch
            {
                KeymapConfigFrames.ParamReadEntry => new byte[] { 0x01, MetaStatus, 0x0A, 0x10, 0x00, 0x01 },
                KeymapConfigFrames.ParamCommit => new byte[] { 0x03, 0x00 },
                _ => new byte[] { param, EntryStatus },
            };
            FrameReceived?.Invoke(this,
                CompanyFrame.Build(CompanyFrame.BaseCmdResponse, CompanyCmd.KeymapConfig, payload));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static CompanyKeymapConfigService NewService(ICommandChannel channel) =>
        new(channel, new SystemClock(), new CompanyConfig());

    private static List<KeymapEntry> FactoryEntries() => KeymapFactoryDefault.Entries.ToList();

    // ================= 全链成功（整表） =================

    [Fact]
    public async Task ApplyAsync_FullTable_AllOk_WritesAllAndCommits()
    {
        var dev = new FakeKeymapDevice();
        var switchEntries = KeymapFactoryDefault.SwitchEntries.ToList();   // 3 行，其中 mode=0 内部行不发

        var report = await NewService(dev).ApplyAsync(FactoryEntries(), switchEntries);

        Assert.True(report.AllOk);
        Assert.True(report.ProbeResponded);
        Assert.Equal(0x0A, report.MetaCount);
        Assert.Equal(0x10, report.MetaMaxEntries);
        Assert.Equal(FactoryEntries().Count + 2, report.Written);          // 10 按键条 + 2 切换行（内部行跳过）
        Assert.True(report.Committed);
        Assert.Null(report.Error);

        // 事务序列：1 探测 + 10 条目(02) + 2 切换(06) + 1 提交(03)
        Assert.Equal(1 + FactoryEntries().Count + 2 + 1, dev.Requests.Count);
        Assert.Equal(KeymapConfigFrames.ParamReadEntry, dev.Requests[0][0]);
        Assert.Equal(KeymapConfigFrames.ParamCommit, dev.Requests[^1][0]);
        // 槽位=存储序下标（0..N-1）
        Assert.Equal((byte)0, dev.Requests[1][1]);
        Assert.Equal((byte)(FactoryEntries().Count - 1), dev.Requests[FactoryEntries().Count][1]);
    }

    [Fact]
    public async Task ApplyAsync_InternalSwitchRowNotSent()
    {
        var dev = new FakeKeymapDevice();

        await NewService(dev).ApplyAsync(FactoryEntries(), KeymapFactoryDefault.SwitchEntries.ToList());

        // 所有 06 请求的 mode 都非内部行
        var switchRequests = dev.Requests.Where(r => r[0] == KeymapConfigFrames.ParamWriteSwitch).ToList();
        Assert.Equal(2, switchRequests.Count);
        Assert.All(switchRequests, r => Assert.NotEqual((byte)0x00, r[1]));
    }

    // ================= 无应答（旧固件现网路径） =================

    [Fact]
    public async Task ApplyAsync_SilentDevice_ProbeNotResponded_NothingApplied()
    {
        var dev = new FakeKeymapDevice { Silent = true };

        var report = await NewService(dev).ApplyAsync(FactoryEntries(), []);

        Assert.False(report.AllOk);
        Assert.False(report.ProbeResponded);
        Assert.Equal("探测无应答", report.Error);
        Assert.False(report.Committed);
        Assert.Equal(0, report.Written);
    }

    // ================= 中途失败（中止语义） =================

    [Fact]
    public async Task ApplyAsync_MetaStatusError_AbortsBeforeWrites()
    {
        var dev = new FakeKeymapDevice { MetaStatus = 0x01 };

        var report = await NewService(dev).ApplyAsync(FactoryEntries(), []);

        Assert.False(report.AllOk);
        Assert.True(report.ProbeResponded);
        Assert.Contains("元信息读失败", report.Error);
        Assert.Equal(0, report.Written);
        Assert.False(report.Committed);
        Assert.Equal(1, dev.Requests.Count);                               // 只发了探测
    }

    [Fact]
    public async Task ApplyAsync_EntryRejected_AbortsAndKeepsCount()
    {
        var dev = new FakeKeymapDevice { EntryStatus = 0x02 };

        var report = await NewService(dev).ApplyAsync(FactoryEntries(), []);

        Assert.False(report.AllOk);
        Assert.Contains("拒绝", report.Error);
        Assert.Equal(0, report.Written);
        Assert.False(report.Committed);
        Assert.Equal(2, dev.Requests.Count);                               // 探测 + 第一个写条目
    }
}
