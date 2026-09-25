// <copyright file="SerialPortHubTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Communication;

namespace PencilBox.Tests;

/// <summary>
/// <see cref="SerialPortHub"/>（06 M1）状态机全量单测：引用计数 / 多读者广播 / 写旁路顺序 / 波特率冲突点名 / 订阅隔离。
/// IO 用 Fake（零真实串口），对应方案 §5 A5 验收。
/// </summary>
public class SerialPortHubTests
{
    private sealed class FakeIo : ISerialPortIo
    {
        public int OpenCount;
        public int CloseCount;
        public List<byte[]> Sent = new();
        public bool OpenResult = true;
        public bool IsOpen { get; set; }

        public event EventHandler<ReadOnlyMemory<byte>>? Received;

        public Task<bool> OpenAsync(string portName, int baudRate, CancellationToken cancellationToken = default)
        {
            OpenCount++;
            IsOpen = OpenResult;
            return Task.FromResult(OpenResult);
        }

        public Task CloseAsync()
        {
            CloseCount++;
            IsOpen = false;
            return Task.CompletedTask;
        }

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            Sent.Add(data.ToArray());
            return Task.CompletedTask;
        }

        /// <summary>模拟设备回字节（读线程触发）</summary>
        public void RaiseReceived(params byte[] data)
            => Received?.Invoke(this, data);
    }

    private static (SerialPortHub hub, Func<FakeIo> created) NewHub()
    {
        FakeIo? last = null;
        SerialPortHub? hubRef = null;
        var created = 0;

        SerialPortHub Factory()
        {
            created++;
            last = new FakeIo();
            return new SerialPortHub(() => last!);
        }

        hubRef = Factory();
        return (hubRef, () => last!);
    }

    // ===== 引用计数 =====

    [Fact]
    public async Task Open_SamePortTwice_OpensPhysicalPortOnlyOnce()
    {
        var (hub, io) = NewHub();

        await hub.OpenAsync("COM20", 115200, SerialPortOwners.Command);
        await hub.OpenAsync("COM20", 115200, SerialPortOwners.Scope);

        Assert.Equal(1, io().OpenCount);
        Assert.True(hub.IsPortOpen("com20"));   // 大小写不敏感
        Assert.Equal(115200, hub.BaudRateOf("COM20"));
    }

    [Fact]
    public async Task Open_SameOwnerRepeatedly_IsIdempotent_SingleCloseReleases()
    {
        var (hub, io) = NewHub();

        await hub.OpenAsync("COM20", 115200, SerialPortOwners.Command);
        await hub.OpenAsync("COM20", 115200, SerialPortOwners.Command);   // 同参与者幂等

        await hub.CloseAsync("COM20", SerialPortOwners.Command);          // 一次即归零

        Assert.False(hub.IsPortOpen("COM20"));
        Assert.Equal(1, io().CloseCount);
    }

    [Fact]
    public async Task Close_OneOwner_OtherOwnerKeepsPortOpen()
    {
        var (hub, io) = NewHub();

        await hub.OpenAsync("COM20", 115200, SerialPortOwners.Command);
        await hub.OpenAsync("COM20", 115200, SerialPortOwners.Scope);

        await hub.CloseAsync("COM20", SerialPortOwners.Command);

        Assert.True(hub.IsPortOpen("COM20"));
        Assert.Equal(0, io().CloseCount);   // 物理口没关
        Assert.Equal(SerialPortOwners.Scope, hub.OwnersOf("COM20"));

        await hub.CloseAsync("COM20", SerialPortOwners.Scope);
        Assert.False(hub.IsPortOpen("COM20"));
        Assert.Equal(1, io().CloseCount);   // 归零才真关
    }

    [Fact]
    public async Task Open_PhysicalFailure_ReturnsFalse_PortNotRegistered()
    {
        FakeIo? last = null;
        var hub = new SerialPortHub(() => last = new FakeIo { OpenResult = false });

        Assert.False(await hub.OpenAsync("COM20", 115200, SerialPortOwners.Command));
        Assert.False(hub.IsPortOpen("COM20"));

        // 失败后重试（换了 IO）仍可成功
        var (hub2, io2) = NewHub();
        Assert.True(await hub2.OpenAsync("COM20", 115200, SerialPortOwners.Command));
        _ = io2;
    }

    // ===== 波特率冲突 =====

    [Fact]
    public async Task Open_DifferentBaudOnOpenPort_ThrowsWithOwnerNamed()
    {
        var (hub, _) = NewHub();

        await hub.OpenAsync("COM20", 115200, SerialPortOwners.Scope);

        var ex = await Assert.ThrowsAsync<SerialPortConflictException>(
            () => hub.OpenAsync("COM20", 1_000_000, SerialPortOwners.Command));

        Assert.Equal("COM20", ex.PortName);
        Assert.Equal(115200, ex.OpenBaudRate);
        Assert.Equal(1_000_000, ex.RequestedBaudRate);
        Assert.Contains(SerialPortOwners.Scope, ex.Owners);
        Assert.Contains("serial-scope", ex.Message);   // 点名谁在用
    }

    // ===== 多读者广播 =====

    [Fact]
    public async Task DeviceBytes_AreBroadcastToAllSubscribers()
    {
        var (hub, io) = NewHub();
        await hub.OpenAsync("COM20", 115200, SerialPortOwners.Command);

        var a = new List<byte>();
        var b = new List<byte>();
        using var s1 = hub.Subscribe("COM20", d => a.AddRange(d.ToArray()));
        using var s2 = hub.Subscribe("COM20", d => b.AddRange(d.ToArray()));

        io().RaiseReceived(0xF5, 0xAA, 0xAA);   // 模拟设备应答（io 读线程触发）

        Assert.Equal(new byte[] { 0xF5, 0xAA, 0xAA }, a);
        Assert.Equal(a, b);                     // 多读者：命令帧装配 / 日志时间轴各得一份
    }

    [Fact]
    public async Task Subscribe_UnopenedPort_Throws()
    {
        var (hub, _) = NewHub();

        Assert.Throws<InvalidOperationException>(() => hub.Subscribe("COM20", _ => { }));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task SubscribedCallback_Throwing_DoesNotAffectOthers()
    {
        var (hub, _) = NewHub();
        await hub.OpenAsync("COM20", 115200, SerialPortOwners.Command);

        var good = new List<byte>();
        using var bad = hub.Subscribe("COM20",
            _ => throw new InvalidOperationException("读旁路崩了"),
            _ => throw new InvalidOperationException("写旁路崩了"));
        using var s = hub.Subscribe("COM20", _ => { }, d => good.AddRange(d.ToArray()));

        await hub.SendAsync("COM20", new byte[] { 1, 2, 3 });   // 写广播路径（读路径同理，同一 Broadcast）

        Assert.Equal(new byte[] { 1, 2, 3 }, good);             // 崩的那个没波及好的
    }

    // ===== 写旁路顺序 =====

    [Fact]
    public async Task Send_BroadcastsOnWritten_BeforePhysicalWrite()
    {
        var (hub, io) = NewHub();
        await hub.OpenAsync("COM20", 115200, SerialPortOwners.Command);

        var order = new List<string>();
        using var s = hub.Subscribe("COM20",
            _ => order.Add("received"),
            _ => order.Add("written"));

        await hub.SendAsync("COM20", new byte[] { 0x01 });

        Assert.Equal(new[] { "written" }, order);               // 写旁路先于物理写
        Assert.Single(io().Sent);                                // 物理写恰好一次
    }

    [Fact]
    public async Task Send_UnopenedPort_Throws()
    {
        var (hub, _) = NewHub();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => hub.SendAsync("COM20", new byte[] { 1 }));
    }

    // ===== 订阅生命周期 =====

    [Fact]
    public async Task DisposeSubscription_StopsDelivery()
    {
        var (hub, io) = NewHub();
        await hub.OpenAsync("COM20", 115200, SerialPortOwners.Command);

        var got = 0;
        var s = hub.Subscribe("COM20", _ => got++);
        s.Dispose();

        await hub.SendAsync("COM20", new byte[] { 1 });   // 写旁路也走订阅
        Assert.Equal(0, got);
        _ = io;
    }

    [Fact]
    public async Task ClosePort_AllSubscriptions_BecomeInert()
    {
        var (hub, _) = NewHub();
        await hub.OpenAsync("COM20", 115200, SerialPortOwners.Command);

        var got = 0;
        var s = hub.Subscribe("COM20", _ => got++, _ => got++);

        await hub.CloseAsync("COM20", SerialPortOwners.Command);

        // 口已关：再开口是**新**口实体，旧订阅不再投递
        await hub.OpenAsync("COM20", 115200, SerialPortOwners.Command);
        await hub.SendAsync("COM20", new byte[] { 1 });

        Assert.Equal(0, got);
        s.Dispose();
    }

    // ===== 场景综合：M1 验收 A1 的纯逻辑等价 =====

    [Fact]
    public async Task Scenario_LogTapsWhileCommandSessionWorks()
    {
        var (hub, _) = NewHub();

        // 命令面先开（读 MAC）
        Assert.True(await hub.OpenAsync("COM20", 115200, SerialPortOwners.Command));

        // 日志面后到：发现口已开 ⇒ 只挂订阅（不开第二句柄）
        var timeline = new List<(string dir, byte[] data)>();
        using var log = hub.Subscribe("COM20",
            d => timeline.Add(("up", d.ToArray())),
            d => timeline.Add(("down", d.ToArray())));

        // 命令面发 0x51 读请求（PC→设备）：日志应先记 ↓
        var request = new byte[] { 0xF5, 0xAA, 0xAA, 0x06, 0x51, 0x00, 0x01, 0x01 };
        await hub.SendAsync("COM20", request);

        // 命令面断开（会话结束）：口上没有别的持有者 ⇒ 物理口关
        await hub.CloseAsync("COM20", SerialPortOwners.Command);
        Assert.False(hub.IsPortOpen("COM20"));

        Assert.Single(timeline);
        Assert.Equal("down", timeline[0].dir);
        Assert.Equal(request, timeline[0].data);
    }

    [Fact]
    public async Task Scenario_ScopeOpensFirst_CommandReusesPort()
    {
        var (hub, io) = NewHub();

        // 日志先开（独占模式注册进 hub）
        Assert.True(await hub.OpenAsync("COM20", 115200, SerialPortOwners.Scope));

        // 命令面后到：同口同波特率 ⇒ 复用，不开第二句柄
        Assert.True(await hub.OpenAsync("COM20", 115200, SerialPortOwners.Command));
        Assert.Equal(1, io().OpenCount);

        // 命令面会话结束：口仍被日志持有，不关
        await hub.CloseAsync("COM20", SerialPortOwners.Command);
        Assert.True(hub.IsPortOpen("COM20"));

        // 日志会话结束：归零，物理口关
        await hub.CloseAsync("COM20", SerialPortOwners.Scope);
        Assert.False(hub.IsPortOpen("COM20"));
        Assert.Equal(1, io().CloseCount);
    }
}
