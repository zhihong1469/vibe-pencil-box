using Microsoft.Extensions.Logging.Abstractions;
using PencilBox.Core.Features;
using PencilBox.Core.Models;
using PencilBox.Core.Services;
using PencilBox.Core.SoftTrigger;
using PencilBox.Infrastructure.Config;
using PencilBox.Infrastructure.Services;
using Xunit;

namespace PencilBox.Tests;

/// <summary>
/// 软触发（13 号动作库键盘族，2026-10-19）：
/// 解析/回述互逆、值域分离防回环（触发域 F17~F24 / 动作禁入保留段）、存储回环、
/// 引擎（fake 钩子 + fake 发腿）：触发→回放、非触发域短路、Toggle 两拍、总开关关→引擎即停。
/// </summary>
public class SoftTriggerTests : IDisposable
{
    private readonly string _dir;

    public SoftTriggerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pencilbox-sttest-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // ===== 解析 / 回述互逆 =====

    [Fact]
    public void ParseSequence_TwoSteps()
    {
        var steps = SoftTriggerTextParser.ParseSequence("F2,ALT+L");

        Assert.Equal(2, steps.Count);
        Assert.Equal((byte)0x3B, steps[0].Usage);              // F2
        Assert.Equal(0, steps[0].Modifier);
        Assert.Equal((byte)0x0F, steps[1].Usage);              // L
        Assert.Equal(0x04, steps[1].Modifier);                 // Alt
    }

    [Fact]
    public void Parser_Describer_RoundTrip()
    {
        var text = "CTRL+SHIFT+S,F7,ENTER";
        var described = SoftTriggerDescriber.DescribeSequence(SoftTriggerTextParser.ParseSequence(text));

        Assert.Equal("Ctrl+Shift+S → F7 → Enter", described);
    }

    [Fact]
    public void ParseStep_UnknownKey_Throws()
    {
        Assert.Throws<ArgumentException>(() => SoftTriggerTextParser.ParseSequence("F2,ABCD"));
        Assert.Throws<ArgumentException>(() => SoftTriggerTextParser.ParseSequence("META+L"));
        Assert.Throws<ArgumentException>(() => SoftTriggerTextParser.ParseSequence("  "));
    }

    // ===== 值域分离（防回环硬约束） =====

    [Fact]
    public void TriggerDomain_OnlyF17To24()
    {
        Assert.True(SoftTriggerKeys.IsTriggerDomain(0x6C));    // F17
        Assert.True(SoftTriggerKeys.IsTriggerDomain(0x73));    // F24
        Assert.False(SoftTriggerKeys.IsTriggerDomain(0x68));   // F13（工作状态自管占用）
        Assert.False(SoftTriggerKeys.IsTriggerDomain(0x6B));   // F16
        Assert.False(SoftTriggerKeys.IsTriggerDomain(0x3B));   // F2（普通键不可触发）
        Assert.False(SoftTriggerKeys.IsTriggerDomain(0x00));   // 未知键
    }

    [Fact]
    public void Validate_RejectsTriggerOutsideDomain()
    {
        var def = ValidDef();
        def.TriggerUsageId = 0x3A;                             // F1：不在触发域
        Assert.Throws<ArgumentException>(() => SoftTriggerKeys.Validate(def));

        def.TriggerUsageId = 0x68;                             // F13：保留段（工作状态自管）
        Assert.Throws<ArgumentException>(() => SoftTriggerKeys.Validate(def));
    }

    [Fact]
    public void Validate_RejectsActionInReservedRange()
    {
        var def = ValidDef();
        def.Steps = [new SoftTriggerStep(0x6A, 0)];            // 动作=F15：回环风险，禁
        Assert.Throws<ArgumentException>(() => SoftTriggerKeys.Validate(def));
    }

    [Fact]
    public void Validate_RejectsBadBounds()
    {
        var def = ValidDef();
        def.Name = "";
        Assert.Throws<ArgumentException>(() => SoftTriggerKeys.Validate(def));

        def.Name = "x";
        def.StepIntervalMs = 5001;
        Assert.Throws<ArgumentException>(() => SoftTriggerKeys.Validate(def));

        def.StepIntervalMs = 0;
        def.RepeatMode = SoftTriggerRepeatMode.Toggle;
        def.RepeatIntervalMs = 10;                             // <20ms 下限（仅 Toggle 校验——Once 无连发）
        Assert.Throws<ArgumentException>(() => SoftTriggerKeys.Validate(def));
    }

    [Fact]
    public void Store_SaveLoadRoundTrip_InvalidRejected()
    {
        var store = new SoftTriggerStore(NullLogger<SoftTriggerStore>.Instance, baseDirectory: _dir);

        store.Save(ValidDef());
        var loaded = store.Load("连点测试");
        Assert.NotNull(loaded);
        Assert.Equal("连点测试", loaded.Name);
        Assert.Equal(SoftTriggerKeys.ParseTriggerName("F17"), loaded.TriggerUsageId);
        Assert.Equal(2, loaded.Steps.Count);

        Assert.Single(store.List());

        var bad = ValidDef();
        bad.Steps = [new SoftTriggerStep(0x73, 0)];            // 动作落进触发域（F24）
        Assert.Throws<ArgumentException>(() => store.Save(bad));

        store.Delete("连点测试");
        Assert.Null(store.Load("连点测试"));

        Assert.Throws<KeyNotFoundException>(() => store.SetEnabled("幽灵", true));
    }

    // ===== 引擎（fake 钩子 + fake 发腿） =====

    private sealed class FakeHook : IGlobalKeyboardHook
    {
        public event Action<int>? KeyDown;
        public bool Started { get; private set; }

        public void Start() => Started = true;
        public void Stop() => Started = false;
        public void Press(int vk) => KeyDown?.Invoke(vk);
    }

    private sealed class FakeSender : ISyntheticKeyboard
    {
        public List<(IReadOnlyList<SoftTriggerStep> Steps, int Interval)> Sent = [];

        public void SendSequence(IReadOnlyList<SoftTriggerStep> steps, int stepIntervalMs) =>
            Sent.Add((steps, stepIntervalMs));
    }

    private (SoftTriggerEngine Engine, FakeHook Hook, FakeSender Sender, FeatureToggleService Toggles) CreateEngine()
    {
        var toggles = new FeatureToggleService(NullLogger<FeatureToggleService>.Instance, baseDirectory: _dir);
        var hook = new FakeHook();
        var sender = new FakeSender();
        var engine = new SoftTriggerEngine(
            new SoftTriggerStore(NullLogger<SoftTriggerStore>.Instance, baseDirectory: _dir),
            hook, sender, toggles,
            NullLogger<SoftTriggerEngine>.Instance);
        return (engine, hook, sender, toggles);
    }

    private static SoftTriggerDefinition ValidDef() => new()
    {
        Name = "连点测试",
        Enabled = true,
        TriggerUsageId = SoftTriggerKeys.ParseTriggerName("F17"),
        Steps = SoftTriggerTextParser.ParseSequence("F2,ALT+L"),
        StepIntervalMs = 60,
        RepeatMode = SoftTriggerRepeatMode.Once,
        RepeatIntervalMs = 200,
    };

    private static async Task<T> WaitForAsync<T>(Func<T?> probe, string what, int timeoutMs = 3000) where T : class
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var hit = probe();
            if (hit != null)
                return hit;
            await Task.Delay(20);
        }
        throw new TimeoutException($"等待超时：{what}");
    }

    [Fact]
    public async Task Engine_TriggerKeyReplaysSequence()
    {
        var (engine, hook, sender, _) = CreateEngine();
        var store = new SoftTriggerStore(NullLogger<SoftTriggerStore>.Instance, baseDirectory: _dir);
        store.Save(ValidDef());
        engine.Start();
        Assert.True(engine.IsRunning);
        Assert.True(hook.Started);

        hook.Press(0x81);                                      // VK F18 → usage 0x6D（无定义）
        hook.Press(0x80);                                      // VK F17 → usage 0x6C（命中）

        var replayed = await WaitForAsync(
            () => engine.RecentEvents(20).FirstOrDefault(e => e.Kind == SoftTriggerEvent.KindReplayed),
            "Replayed 事件");
        Assert.Equal("连点测试", replayed.Name);
        Assert.Single(sender.Sent);
        Assert.Equal(2, sender.Sent[0].Steps.Count);
    }

    [Fact]
    public async Task Engine_NonTriggerKeyIgnored()
    {
        var (engine, hook, sender, _) = CreateEngine();
        var store = new SoftTriggerStore(NullLogger<SoftTriggerStore>.Instance, baseDirectory: _dir);
        store.Save(ValidDef());
        engine.Start();

        hook.Press(0x76);                                      // VK F7：普通键
        hook.Press(0x7F);                                      // VK F22：触发域内但无定义
        await Task.Delay(150);

        Assert.Empty(sender.Sent);
        Assert.DoesNotContain(engine.RecentEvents(50), e => e.Kind == SoftTriggerEvent.KindReplayed);
    }

    [Fact]
    public async Task Engine_ToggleTwoPress_StartThenStop()
    {
        var (engine, hook, sender, _) = CreateEngine();
        var store = new SoftTriggerStore(NullLogger<SoftTriggerStore>.Instance, baseDirectory: _dir);
        var def = ValidDef();
        def.RepeatMode = SoftTriggerRepeatMode.Toggle;
        def.RepeatIntervalMs = 60_000;                         // 测试窗内不让周期定时器二发
        store.Save(def);
        engine.Start();

        hook.Press(0x80);                                      // 第一拍：连发开始
        await WaitForAsync(
            () => engine.RecentEvents(20).FirstOrDefault(e => e.Kind == SoftTriggerEvent.KindToggleStarted),
            "ToggleStarted");
        var afterStart = sender.Sent.Count;
        Assert.Equal(1, afterStart);                           // 立即回放第一遍

        await Task.Delay(100);
        Assert.True(sender.Sent.Count <= 2, "60s 周期下测试窗内不应二发");

        hook.Press(0x80);                                      // 第二拍：连发停止
        var stopped = await WaitForAsync(
            () => engine.RecentEvents(20).FirstOrDefault(e => e.Kind == SoftTriggerEvent.KindToggleStopped),
            "ToggleStopped");
        Assert.Equal("连点测试", stopped.Name);
    }

    [Fact]
    public void Engine_StartRejectedWhenFeatureOff()
    {
        var (engine, _, _, toggles) = CreateEngine();
        toggles.SetEnabled("softtrigger", false);

        Assert.Throws<InvalidOperationException>(() => engine.Start());
        Assert.False(engine.IsRunning);
    }

    [Fact]
    public async Task Engine_FeatureOffWhileRunning_StopsImmediately()
    {
        var (engine, hook, sender, toggles) = CreateEngine();
        var store = new SoftTriggerStore(NullLogger<SoftTriggerStore>.Instance, baseDirectory: _dir);
        store.Save(ValidDef());
        engine.Start();
        Assert.True(engine.IsRunning);

        toggles.SetEnabled("softtrigger", false);              // 14 号：关闭=运行中引擎即停
        Assert.False(engine.IsRunning);
        // 钩子本体不停（工作状态监听共用）——停机证据看"停后触发不再回放"

        hook.Press(0x80);                                      // 停后再按 F17：不得回放
        await Task.Delay(150);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public void Engine_Fire_DisabledDefRejected()
    {
        var (engine, _, sender, _) = CreateEngine();
        var store = new SoftTriggerStore(NullLogger<SoftTriggerStore>.Instance, baseDirectory: _dir);
        var def = ValidDef();
        def.Enabled = false;
        store.Save(def);

        Assert.Throws<InvalidOperationException>(() => engine.Fire("连点测试"));
        Assert.Empty(sender.Sent);

        Assert.Throws<KeyNotFoundException>(() => engine.Fire("不存在"));
    }
}
