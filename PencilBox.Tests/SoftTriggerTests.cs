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
    public void Validate_RejectsBadTriggerAndWorkStateAction()
    {
        var def = ValidDef();
        def.TriggerUsageId = 0x00;                             // 不可映射
        Assert.Throws<ArgumentException>(() => SoftTriggerKeys.Validate(def));

        def = ValidDef();
        def.TriggerUsageId = 0x68;                             // F13：工作状态保留段
        Assert.Throws<ArgumentException>(() => SoftTriggerKeys.Validate(def));

        def = ValidDef();
        def.Steps = [new SoftTriggerStep(0x6A, 0)];            // 动作=F15：工作状态保留段禁
        Assert.Throws<ArgumentException>(() => SoftTriggerKeys.Validate(def));
    }

    [Fact]
    public void Validate_RejectsBadBounds()
    {
        var def = ValidDef();
        def.Name = "";
        Assert.Throws<ArgumentException>(() => SoftTriggerKeys.Validate(def));

        def.Name = "x";
        def.Steps = [new SoftTriggerStep(0x04, 0, 60_001)];    // 步后延时 >60s 上限
        Assert.Throws<ArgumentException>(() => SoftTriggerKeys.Validate(def));

        def.Steps = [new SoftTriggerStep(0x04, 0)];
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
        Assert.Equal(SoftTriggerTextParser.ParseTrigger("F17").Usage, loaded.TriggerUsageId);
        Assert.Equal(2, loaded.Steps.Count);

        Assert.Single(store.List());

        var bad = ValidDef();
        bad.Steps = [new SoftTriggerStep(bad.TriggerUsageId, 0)];   // 动作含自己触发键（自环）
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

        public void SendSequence(IReadOnlyList<SoftTriggerStep> steps) =>
            Sent.Add((steps, 0));
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

    // ===== v2：步后延时 / 全键盘触发池 / 动态预留全局律 =====

    [Fact]
    public void ParseStep_DelaySuffix()
    {
        var steps = SoftTriggerTextParser.ParseSequence("F2@100,ALT+L@250,ENTER");

        Assert.Equal(100, steps[0].DelayMsBefore);
        Assert.Equal(250, steps[1].DelayMsBefore);
        Assert.Equal(0, steps[2].DelayMsBefore);
        Assert.Equal("F2@100 → Alt+L@250 → Enter", SoftTriggerDescriber.DescribeSequence(steps));
    }

    [Fact]
    public void TriggerPool_FullKeyboard_ExceptWorkState()
    {
        Assert.True(SoftTriggerKeys.IsTriggerCandidate(0x04));  // A
        Assert.True(SoftTriggerKeys.IsTriggerCandidate(0x3B));  // F2
        Assert.True(SoftTriggerKeys.IsTriggerCandidate(0x6C));  // F17
        Assert.True(SoftTriggerKeys.IsTriggerCandidate(0x28));  // Enter
        Assert.False(SoftTriggerKeys.IsTriggerCandidate(0x68)); // F13 工作状态保留
        Assert.False(SoftTriggerKeys.IsTriggerCandidate(0x6B)); // F16
        Assert.False(SoftTriggerKeys.IsTriggerCandidate(0x00)); // 未知
        Assert.False(SoftTriggerKeys.IsWorkStateReserved(0x6C));// F17 不属工作状态段
    }

    [Fact]
    public void Validate_RejectsWorkStateTriggerAndSelfTrigger()
    {
        var def = ValidDef();
        def.TriggerUsageId = 0x69;                              // F14 工作状态保留
        Assert.Throws<ArgumentException>(() => SoftTriggerKeys.Validate(def));

        def = ValidDef();
        def.TriggerUsageId = SoftTriggerTextParser.ParseTrigger("F2").Usage;
        def.Steps = [new SoftTriggerStep(0x3B, 0)];             // 动作含自己触发键 F2
        Assert.Throws<ArgumentException>(() => SoftTriggerKeys.Validate(def));
    }

    [Fact]
    public void ValidateGlobal_CrossDefinitionLoopRejected()
    {
        // A 触发 F2；B 动作含 F2 → 冲突（B 回放会真实触发 A → 链）
        var a = ValidDef();
        a.Name = "A";
        a.TriggerUsageId = SoftTriggerTextParser.ParseTrigger("F2").Usage;
        a.Steps = [new SoftTriggerStep(0x0F, 0)];               // L

        var b = ValidDef();
        b.Name = "B";
        b.TriggerUsageId = SoftTriggerTextParser.ParseTrigger("F3").Usage;
        b.Steps = [new SoftTriggerStep(0x3B, 0)];               // F2 ← 撞 A 的触发
        Assert.Throws<ArgumentException>(() => SoftTriggerKeys.ValidateGlobal([a, b]));

        // 反向：C 触发 L（A 的动作键）→ 同样冲突
        var c = ValidDef();
        c.Name = "C";
        c.TriggerUsageId = 0x0F;                                // L
        c.Steps = [new SoftTriggerStep(0x10, 0)];               // M
        Assert.Throws<ArgumentException>(() => SoftTriggerKeys.ValidateGlobal([a, c]));

        // 动作相同不冲突：两定义动作都含 L（不触发）合法
        var d = ValidDef();
        d.Name = "D";
        d.TriggerUsageId = SoftTriggerTextParser.ParseTrigger("F5").Usage;
        d.Steps = [new SoftTriggerStep(0x0F, 0)];               // L ← 与 A 动作同键，合法
        SoftTriggerKeys.ValidateGlobal([a, d]);

        // 修饰精确区分：E 触发 ALT+F2（A 裸 F2）不冲突
        var e = ValidDef();
        e.Name = "E";
        e.TriggerUsageId = 0x3B;
        e.TriggerModifier = 0x04;                               // ALT+F2
        e.Steps = [new SoftTriggerStep(0x10, 0)];
        SoftTriggerKeys.ValidateGlobal([a, e]);

        // 但 F 动作含裸 F2（与 A 触发精确对相同）→ 冲突
        var f = ValidDef();
        f.Name = "F";
        f.TriggerUsageId = SoftTriggerTextParser.ParseTrigger("F6").Usage;
        f.Steps = [new SoftTriggerStep(0x3B, 0)];
        Assert.Throws<ArgumentException>(() => SoftTriggerKeys.ValidateGlobal([a, f]));
    }

    [Fact]
    public void Store_MigratesLegacyStepInterval()
    {
        var store = new SoftTriggerStore(NullLogger<SoftTriggerStore>.Instance, baseDirectory: _dir);
        var legacyJson = "{\"Name\":\"旧版\",\"Enabled\":true,\"TriggerUsageId\":109,\"TriggerModifier\":0," +
            "\"Steps\":[{\"Usage\":59,\"Modifier\":0},{\"Usage\":15,\"Modifier\":4}],\"StepIntervalMs\":80," +
            "\"RepeatMode\":0,\"RepeatIntervalMs\":200,\"Note\":\"\",\"CreatedAt\":\"2026-10-19T10:00:00\"}";
        Directory.CreateDirectory(Path.Combine(_dir, "configs", "softtriggers"));
        File.WriteAllText(Path.Combine(_dir, "configs", "softtriggers", "旧版.json"), legacyJson);

        var def = store.Load("旧版");
        Assert.NotNull(def);
        Assert.Equal(0, def.StepIntervalMs);                    // 字段退役清零
        Assert.All(def.Steps, s => Assert.Equal(80, s.DelayMsBefore));  // 摊给未显式延时步
    }

    [Fact]
    public void Store_MigratesLegacyDelayFieldName()
    {
        var store = new SoftTriggerStore(NullLogger<SoftTriggerStore>.Instance, baseDirectory: _dir);
        var legacyJson = "{\"Name\":\"旧延时\",\"Enabled\":true,\"TriggerUsageId\":109,\"TriggerModifier\":0," +
            "\"Steps\":[{\"Usage\":59,\"Modifier\":4,\"DelayMsAfter\":250}],\"StepIntervalMs\":0," +
            "\"RepeatMode\":0,\"RepeatIntervalMs\":200,\"Note\":\"\",\"CreatedAt\":\"2026-10-19T10:00:00\"}";
        Directory.CreateDirectory(Path.Combine(_dir, "configs", "softtriggers"));
        File.WriteAllText(Path.Combine(_dir, "configs", "softtriggers", "旧延时.json"), legacyJson);

        var def = store.Load("旧延时");
        Assert.NotNull(def);
        Assert.Equal(250, def.Steps[0].DelayMsBefore);          // v2.3 旧步后字段改名入步前（值随步走）
    }

    [Fact]
    public void Store_SaveRejectedWhenConflictsWithEnabledDef()
    {
        var store = new SoftTriggerStore(NullLogger<SoftTriggerStore>.Instance, baseDirectory: _dir);
        var a = ValidDef();
        a.Name = "甲";
        a.TriggerUsageId = SoftTriggerTextParser.ParseTrigger("F2").Usage;
        a.Steps = [new SoftTriggerStep(0x0F, 0)];
        store.Save(a);

        var b = ValidDef();
        b.Name = "乙";
        b.TriggerUsageId = SoftTriggerTextParser.ParseTrigger("F3").Usage;
        b.Steps = [new SoftTriggerStep(0x3B, 0)];               // 动作 F2 撞甲的触发
        Assert.Throws<ArgumentException>(() => store.Save(b));

        // 停用甲后不占坑：乙可存
        store.SetEnabled("甲", enabled: false);
        store.Save(b);
        Assert.NotNull(store.Load("乙"));
    }

    private static SoftTriggerDefinition ValidDef() => new()
    {
        Name = "连点测试",
        Enabled = true,
        TriggerUsageId = SoftTriggerTextParser.ParseTrigger("F17").Usage,
        Steps = SoftTriggerTextParser.ParseSequence("F2,ALT+L"),
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
    public async Task Engine_WaitsForTriggerReleaseBeforeReplay()
    {
        var (engine, hook, sender, _) = CreateEngine();
        var store = new SoftTriggerStore(NullLogger<SoftTriggerStore>.Instance, baseDirectory: _dir);
        store.Save(ValidDef());
        engine.Start();

        var polls = 0;
        SoftTriggerEngine.IsKeyDownOverride = _ => ++polls <= 2;   // 头两次查询=仍按着，第三次=已松开
        try
        {
            hook.Press(0x80);                                      // VK F17 → usage 0x6C（命中）
            var replayed = await WaitForAsync(
                () => engine.RecentEvents(20).FirstOrDefault(e => e.Kind == SoftTriggerEvent.KindReplayed),
                "Replayed 事件");
            Assert.NotNull(replayed);
            Assert.True(polls >= 2, $"应先轮询等触发键松开再回放（实际 polls={polls}）");
            Assert.Single(sender.Sent);                            // 松开后照常回放（不吞动作）
        }
        finally
        {
            SoftTriggerEngine.IsKeyDownOverride = null;            // 注入口必须复原，防污染并行用例
        }
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
