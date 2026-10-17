using Microsoft.Extensions.Logging.Abstractions;
using PencilBox.Core.Models;
using PencilBox.Core.Protocol;
using PencilBox.Infrastructure.Config;
using Xunit;

namespace PencilBox.Tests;

/// <summary>
/// 出厂默认 Keymap 表与命名配置存储（pc-issues/04 提案 A/B）。
/// 出厂表字节级对拍固件 `srv_keymap.c s_keymap_default` 前 10 条——固件表变更时本测试会红，提示同步。
/// 固件末行预留组合键 {0xF0,0x05} 无发送方、不属产品运行动作，经裁决（2026-10-08）不进 PC 表。
/// </summary>
public class KeymapConfigStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _legacy;

    public KeymapConfigStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pencilbox-kmtest-" + Guid.NewGuid().ToString("N"));
        _legacy = Path.Combine(_dir, "legacy-keymap.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private KeymapConfigStore CreateStore() => new(
        NullLogger<KeymapConfigStore>.Instance,
        baseDirectory: _dir,
        legacyCachePath: _legacy);

    // ===== 提案 A：出厂默认表字节级对拍固件 =====

    [Fact]
    public void FactoryDefault_MatchesFirmwareTable()
    {
        var e = KeymapFactoryDefault.Entries;

        Assert.Equal(10, e.Length);

        // {key_id, event, action, mod, code_lo, ...} —— 逐条对拍 srv_keymap.c（固件预留行 0xF0 已摘，见类注释）
        Assert.Equal(new byte[] { 0x03, 0x01, 0x01, 0x00, 0x4B, 0x00, 0x00, 0x00 }, e[0].ToBytes()); // K3 CLICK → PageUp
        Assert.Equal(new byte[] { 0x04, 0x01, 0x01, 0x00, 0x4E, 0x00, 0x00, 0x00 }, e[1].ToBytes()); // K4 CLICK → PageDown
        Assert.Equal(new byte[] { 0x05, 0x01, 0x03, 0x00, 0xE9, 0x00, 0x00, 0x00 }, e[2].ToBytes()); // K5 CLICK → VOL+
        Assert.Equal(new byte[] { 0x06, 0x01, 0x03, 0x00, 0xEA, 0x00, 0x00, 0x00 }, e[3].ToBytes()); // K6 CLICK → VOL−
        Assert.Equal(new byte[] { 0x03, 0x03, 0x01, 0x00, 0x4B, 0x00, 0x00, 0x00 }, e[4].ToBytes()); // K3 LONG_PRESS → PageUp
        Assert.Equal(new byte[] { 0x04, 0x03, 0x01, 0x00, 0x4E, 0x00, 0x00, 0x00 }, e[5].ToBytes()); // K4 LONG_PRESS → PageDown
        Assert.Equal(new byte[] { 0x05, 0x03, 0x03, 0x00, 0xE9, 0x00, 0x00, 0x00 }, e[6].ToBytes()); // K5 LONG_PRESS → VOL+
        Assert.Equal(new byte[] { 0x06, 0x03, 0x03, 0x00, 0xEA, 0x00, 0x00, 0x00 }, e[7].ToBytes()); // K6 LONG_PRESS → VOL−
        Assert.Equal(new byte[] { 0x02, 0x03, 0x01, 0x00, 0x68, 0x00, 0x00, 0x00 }, e[8].ToBytes()); // K2 语音会话开始 → F13（2026-10-15 定稿：无修饰键）
        Assert.Equal(new byte[] { 0x02, 0x04, 0x01, 0x00, 0x69, 0x00, 0x00, 0x00 }, e[9].ToBytes()); // K2 语音会话结束 → F14
    }

    // ===== 提案 B：命名配置存储 =====

    [Fact]
    public void Initialize_CreatesFactoryConfig_RegeneratesOnDrift()
    {
        var store = CreateStore();
        store.Initialize();

        var doc = store.Load(store.FactoryName);
        Assert.NotNull(doc);
        Assert.Equal(10, doc!.Entries.Count);

        // 用户手改出厂默认文件 → 再 Initialize 被常量表覆盖回（单一来源）
        File.WriteAllText(Path.Combine(_dir, "configs", "keymap", $"{store.FactoryName}.json"),
            """{"Name":"出厂默认","Entries":[]}""");
        store.Initialize();
        Assert.Equal(10, store.Load(store.FactoryName)!.Entries.Count);
    }

    [Fact]
    public void Save_Load_Roundtrip()
    {
        var store = CreateStore();
        store.Initialize();

        // 复制列表并**克隆条目**——常量数组里的对象是共享的，绝不可原地改
        var entries = new List<KeymapEntry>(KeymapFactoryDefault.Entries);
        entries[0] = new KeymapEntry { KeyId = 0x03, EventType = KeyEventType.Click, ActionType = KeyActionType.Keyboard, CodeLo = 0x50 };
        store.Save("配置一", entries);

        var doc = store.Load("配置一");
        Assert.NotNull(doc);
        Assert.Equal("配置一", doc!.Name);
        Assert.Equal(10, doc.Entries.Count);
        // 存储按 K1→K6 重排，不能按下标找——按（键,事件）定位改动条目
        var k3Click = doc.Entries.Single(e => e.KeyId == 0x03 && e.EventType == KeyEventType.Click);
        Assert.Equal(0x50, k3Click.CodeLo);
        Assert.Contains("配置一", store.ListNames());
    }

    [Fact]
    public void Rename_Delete_CurrentTracking()
    {
        var store = CreateStore();
        store.Initialize();
        store.Save("配置一", KeymapFactoryDefault.Entries);
        store.SetCurrentName("配置一");

        store.Rename("配置一", "配置二");
        Assert.Null(store.Load("配置一"));
        Assert.Equal("配置二", store.Load("配置二")!.Name);
        Assert.Equal("配置二", store.GetCurrentName());   // 当前指向随改名迁移

        Assert.Throws<IOException>(() => store.Rename("配置二", "配置二"));   // 目标已存在

        store.Delete("配置二");
        Assert.Null(store.Load("配置二"));
        Assert.Equal(store.FactoryName, store.GetCurrentName());   // 删当前 → 回落出厂默认
    }

    [Fact]
    public void FactoryConfig_IsReadOnly()
    {
        var store = CreateStore();
        store.Initialize();

        Assert.Throws<InvalidOperationException>(() => store.Delete(store.FactoryName));
        Assert.Throws<InvalidOperationException>(() => store.Rename(store.FactoryName, "X"));
        Assert.Throws<InvalidOperationException>(() => store.Save(store.FactoryName, KeymapFactoryDefault.Entries));
    }

    [Fact]
    public void LegacyCache_MigratesOnce_OriginalKept()
    {
        var store = CreateStore();
        store.Initialize();
        File.WriteAllText(_legacy, """[{"KeyId":3,"EventType":1,"ActionType":1,"Modifier":0,"CodeLo":75,"CodeHi":0,"CodeEx":0,"Reserved":0}]""");

        store.Initialize();   // 再次初始化触发迁移

        var doc = store.Load("本地缓存");
        Assert.NotNull(doc);
        Assert.Single(doc!.Entries);
        Assert.Equal(0x4B, doc.Entries[0].CodeLo);
        Assert.True(File.Exists(_legacy));   // 原文件保留

        store.Initialize();   // 幂等：不重复迁移/不报错
        Assert.NotNull(store.Load("本地缓存"));
    }

    [Fact]
    public void GetCurrentName_FallsBackToFactory_WhenMissingOrInvalid()
    {
        var store = CreateStore();
        store.Initialize();

        Assert.Equal(store.FactoryName, store.GetCurrentName());

        File.WriteAllText(Path.Combine(_dir, "configs", "keymap", ".current"), "不存在配置");
        Assert.Equal(store.FactoryName, store.GetCurrentName());
    }

    // ===== 规范排序（2026-10-08 用户定版：K1→K6 → 事件序 → 组合键垫底） =====

    [Fact]
    public void CanonicalSort_KeysThenEventsCombosLast()
    {
        var scrambled = new List<KeymapEntry>
        {
            new() { KeyId = 0xF0, EventType = KeyEventType.LongSpecial },   // 组合键 → 垫底
            new() { KeyId = 0x05, EventType = KeyEventType.LongPress },
            new() { KeyId = 0x03, EventType = KeyEventType.LongPress },
            new() { KeyId = 0x02, EventType = KeyEventType.LongRelease },
            new() { KeyId = 0x03, EventType = KeyEventType.Click },
            new() { KeyId = 0x02, EventType = KeyEventType.LongPress },     // 同键按事件序
        };

        var sorted = KeymapSorting.Canonical(scrambled);

        Assert.Equal((byte)0x02, sorted[0].KeyId);
        Assert.Equal(KeyEventType.LongPress, sorted[0].EventType);
        Assert.Equal((byte)0x02, sorted[1].KeyId);
        Assert.Equal(KeyEventType.LongRelease, sorted[1].EventType);
        Assert.Equal((byte)0x03, sorted[2].KeyId);
        Assert.Equal(KeyEventType.Click, sorted[2].EventType);
        Assert.Equal((byte)0x03, sorted[3].KeyId);
        Assert.Equal((byte)0x05, sorted[4].KeyId);
        Assert.Equal((byte)0xF0, sorted[5].KeyId);   // 组合键恒最后
    }

    [Fact]
    public void Store_NormalizesOrder_OnLoadAndSave()
    {
        var store = CreateStore();

        // 手写一个乱序文件 → Load 归一化为规范序
        Directory.CreateDirectory(Path.Combine(_dir, "configs", "keymap"));
        File.WriteAllText(Path.Combine(_dir, "configs", "keymap", "乱序.json"),
            """{"Name":"乱序","Entries":[{"KeyId":6,"EventType":1},{"KeyId":2,"EventType":3},{"KeyId":2,"EventType":4}]}""");

        var doc = store.Load("乱序")!;
        Assert.Equal((byte)0x02, doc.Entries[0].KeyId);
        Assert.Equal((byte)0x02, doc.Entries[1].KeyId);
        Assert.Equal((byte)0x06, doc.Entries[2].KeyId);

        // Save 同样归一化
        store.Save("乱序", new List<KeymapEntry>
        {
            new() { KeyId = 0xF0 }, new() { KeyId = 0x04 },
        });
        var saved = store.Load("乱序")!;
        Assert.Equal((byte)0x04, saved.Entries[0].KeyId);
        Assert.Equal((byte)0xF0, saved.Entries[1].KeyId);
    }

    [Fact]
    public void FactoryDefault_File_IsCanonicalOrder_ContentUnchanged()
    {
        var store = CreateStore();
        store.Initialize();

        var doc = store.Load(store.FactoryName)!;

        // 顺序规范化：首位 = K2 语音会话开始（F13），末位 = K6 长按（VOL−）
        Assert.Equal(new byte[] { 0x02, 0x03, 0x01, 0x00, 0x68, 0x00, 0x00, 0x00 }, doc.Entries[0].ToBytes());
        Assert.Equal(new byte[] { 0x06, 0x03, 0x03, 0x00, 0xEA, 0x00, 0x00, 0x00 }, doc.Entries[^1].ToBytes());
        Assert.Equal(new byte[] { 0x03, 0x01, 0x01, 0x00, 0x4B, 0x00, 0x00, 0x00 }, doc.Entries[2].ToBytes()); // K3 单击

        // 内容零增减：与固件常量按 (key,event) 多重集合一致
        Assert.Equal(10, doc.Entries.Count);
        foreach (var e in KeymapFactoryDefault.Entries)
            Assert.Contains(doc.Entries, x => x.ToBytes().SequenceEqual(e.ToBytes()));
    }

    // ===== CLI 解析辅助（Core/Models/KeymapTextParser，CLI/测试共用） =====

    [Fact]
    public void SwitchTable_MatchesFirmware_ByteLevel()
    {
        // S1 档位切换表（固件 srv_keymap.c s_switch_default 2026-10-15 定稿版，3B 条目 [mode][mod][code]）
        var s = KeymapFactoryDefault.SwitchEntries;
        Assert.Equal(3, s.Length);
        Assert.Equal(new byte[] { 0x02, 0x00, 0x6A }, s[0].ToBytes()); // 扩音(SPK) → F15
        Assert.Equal(new byte[] { 0x01, 0x00, 0x6B }, s[1].ToBytes()); // 识别(ON)  → F16
        Assert.Equal(new byte[] { 0x00, 0x00, 0x6B }, s[2].ToBytes()); // 待机(IDLE) → F16

        // 档位名（2026-10-17 用户定版"言多必失"：显示一律物理丝印 ON/SPK；S1 产品语义=扩音开关；
        // mode=0=协议内部值非开关档。固件证据：笔端 0x16 只发 1/2、OFF 档掉电不上报、基站 IDLE 行仅断联补发消费）
        Assert.Equal("IDLE·内部", KeymapFormatter.GetModeName(0x00));
        Assert.Equal("ON", KeymapFormatter.GetModeName(0x01));
        Assert.Equal("SPK", KeymapFormatter.GetModeName(0x02));
        Assert.Equal("F15", KeymapFormatter.GetCodeName(KeyActionType.Keyboard, 0x6A, 0));
        Assert.Equal("F16", KeymapFormatter.GetCodeName(KeyActionType.Keyboard, 0x6B, 0));

        // 内部行锁（一行文案，证据链在 pc-issues/05 §8-6）
        Assert.True(KeymapRules.IsSwitchInternalRow(0x00));
        Assert.False(KeymapRules.IsSwitchInternalRow(0x01));
        Assert.False(KeymapRules.IsSwitchInternalRow(0x02));
        Assert.Contains("内部行", KeymapRules.SwitchIdleLockReason);
    }

    [Fact]
    public void KeyName_K5K6_SymmetricBareNames()
    {
        // 2026-10-17 用户定版：K5"功能"旧描述废弃，K5/K6 裸名对称（描述名未定前勿自造）
        Assert.Equal("K5", KeymapFormatter.GetKeyName(0x05));
        Assert.Equal("K6", KeymapFormatter.GetKeyName(0x06));
    }

    [Fact]
    public void SwitchEntries_Roundtrip_LegacyFallback_FactoryCarried()
    {
        var store = CreateStore();
        store.Initialize();

        // ① 随配置保存并往返（存储按档位序归一，乱序给入）
        store.Save("配置一", KeymapFactoryDefault.Entries,
        [
            new KeymapSwitchEntry { Mode = 0x01, Modifier = 0x04, CodeLo = 0x0A },
            new KeymapSwitchEntry { Mode = 0x00, Modifier = 0x00, CodeLo = 0x2C },
        ]);
        var doc = store.Load("配置一")!;
        Assert.NotNull(doc.SwitchEntries);
        Assert.Equal(2, doc.SwitchEntries!.Count);
        Assert.Equal(0x00, doc.SwitchEntries[0].Mode);   // 档位序：待机在前
        Assert.Equal(0x2C, doc.SwitchEntries[0].CodeLo);
        Assert.Equal(0x04, doc.SwitchEntries[1].Modifier);

        // ② 不带切换表保存 → 文件不携带（消费方回落出厂真值）
        store.Save("配置二", KeymapFactoryDefault.Entries);
        Assert.Null(store.Load("配置二")!.SwitchEntries);

        // ③ 出厂默认文件自带切换表（每次 Initialize 再生；存储按档位序，与常量按多重集合比对）
        var factory = store.Load(store.FactoryName)!;
        Assert.NotNull(factory.SwitchEntries);
        Assert.Equal(3, factory.SwitchEntries!.Count);
        foreach (var e in KeymapFactoryDefault.SwitchEntries)
            Assert.Contains(factory.SwitchEntries, x => x.ToBytes().SequenceEqual(e.ToBytes()));
    }

    [Fact]
    public void K2_EventLock_And_PurposeDisplay()
    {
        // 作用化显示（pc-issues/04 §2A）：K2 的 0x03/0x04 按"语音会话开始/结束"描述
        Assert.Equal("会话开始", KeymapFormatter.GetEventDisplay(0x02, KeyEventType.LongPress));
        Assert.Equal("会话结束", KeymapFormatter.GetEventDisplay(0x02, KeyEventType.LongRelease));
        Assert.Equal("单击", KeymapFormatter.GetEventDisplay(0x02, KeyEventType.Click));      // 违规值兜底照常显示
        Assert.Equal("长按", KeymapFormatter.GetEventDisplay(0x03, KeyEventType.LongPress)); // 非 K2 不受影响

        // 事件列锁定规则（笔↔Dongle 会话边界契约）
        Assert.True(KeymapRules.IsK2EventViolation(0x02, KeyEventType.Click));
        Assert.True(KeymapRules.IsK2EventViolation(0x02, KeyEventType.LongSpecial));
        Assert.False(KeymapRules.IsK2EventViolation(0x02, KeyEventType.LongPress));
        Assert.False(KeymapRules.IsK2EventViolation(0x02, KeyEventType.LongRelease));
        Assert.False(KeymapRules.IsK2EventViolation(0x03, KeyEventType.Click));

        // 2026-10-15 定稿键值：F13~F16 无修饰键（物理键盘不存在、系统零冲突）
        Assert.Equal("F13", KeymapFormatter.GetCodeName(KeyActionType.Keyboard, 0x68, 0));
        Assert.Equal("F14", KeymapFormatter.GetCodeName(KeyActionType.Keyboard, 0x69, 0));
        Assert.Equal("—", KeymapFormatter.GetModifierName(0x00));
        Assert.Equal("Ctrl+Alt", KeymapFormatter.GetModifierName(0x05));
    }

    [Fact]
    public void Cli_ParseHelpers()
    {
        Assert.Equal(0x03, KeymapTextParser.ParseKeyId("K3"));
        Assert.Equal(0x03, KeymapTextParser.ParseKeyId("0x03"));
        Assert.Equal(0xF0, KeymapTextParser.ParseKeyId("0xF0"));
        Assert.Equal(KeyEventType.LongPress, KeymapTextParser.ParseEvent("long"));
        Assert.Equal(KeyEventType.LongRelease, KeymapTextParser.ParseEvent("4"));
        Assert.Equal(KeyActionType.Keyboard, KeymapTextParser.ParseAction("kb"));
        Assert.Equal(0x04, KeymapTextParser.ParseModifier("alt"));
        Assert.Equal(0x03, KeymapTextParser.ParseModifier("ctrl+shift"));   // 0x01|0x02
        Assert.Equal(0x4B, KeymapTextParser.ParseByte("0x4B"));
        Assert.Equal(0x4B, KeymapTextParser.ParseByte("75"));

        Assert.Throws<ArgumentException>(() => KeymapTextParser.ParseEvent("weekly"));
        Assert.Throws<InvalidOperationException>(() => KeymapTextParser.ParseModifier("meta"));
    }
}
