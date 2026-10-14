// <copyright file="AirMouseWorkbenchTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Protocol.Company;
using PencilBox.Core.Services;
using PencilBox.Infrastructure.Config;

namespace PencilBox.Tests;

/// <summary>
/// 空鼠调参工作台（issues-pencil/03）配套单测：
/// 内置参数表的步长/宏名对拍（03 §3/§6 单一来源）、宏片段生成黄金向量（03 §6 示例）、快照存储往返。
/// </summary>
public class AirMouseWorkbenchTests : IDisposable
{
    private readonly string _tmpDir;

    public AirMouseWorkbenchTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "pencilbox-tests-" + Guid.NewGuid().ToString("N")[..8]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmpDir))
            Directory.Delete(_tmpDir, recursive: true);
    }

    // ================= 参数表：步长 / 宏名（03 卷 §3/§6 对拍） =================

    [Fact]
    public void TuningParamTable_StepsAndMacros_MatchDoc03()
    {
        var expected = new (byte Id, double Step, string Macro)[]
        {
            (0x01, 0.05, "CONFIG_AIRMOUSE_SENSITIVITY"),
            (0x02, 0.05, "CONFIG_AIRMOUSE_SLOW_GAIN"),
            (0x03, 1.0,  "CONFIG_AIRMOUSE_SLOW_KNEE_DPS"),
            (0x04, 1.0,  "CONFIG_AIRMOUSE_SLOW_WIDTH_DPS"),
            (0x05, 0.1,  "CONFIG_AIRMOUSE_DEADZONE_DPS"),
            (0x06, 25.0, "CONFIG_AIRMOUSE_FREEZE_RELEASE_MS"),
            // 零偏 v2（01 卷 §5.1）：不入宏（MacroName 空），0x09/0x0A 只读步长 0、0x0B 步长 1
            (0x07, 0.05, ""),
            (0x08, 0.05, ""),
            (0x09, 0.0,  ""),
            (0x0A, 0.0,  ""),
            (0x0B, 1.0,  ""),
        };

        Assert.Equal(11, TuningParamTable.All.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            var d = TuningParamTable.All[i];
            Assert.Equal(expected[i].Id, d.Id);
            Assert.Equal(expected[i].Step, d.Step, 5);
            Assert.Equal(expected[i].Macro, d.MacroName);
            Assert.False(string.IsNullOrWhiteSpace(d.Guide), $"0x{d.Id:X2} 调法文案不得为空（03 卷 §3 内嵌文案）");
        }
    }

    // ================= 宏片段生成（03 卷 §6 黄金向量） =================

    [Fact]
    public void MacroSnippet_Build_GoldenFormat()
    {
        var readings = new List<TuningParamReading>
        {
            new(0x01, 0.35f), new(0x02, 0.22f), new(0x03, 10.0f),
            new(0x04, 8.0f),  new(0x05, 0.5f),  new(0x06, 250f),
        };

        var text = AirMouseMacroSnippet.Build(readings, "MODEL=0x11  SVER=0.12.119", "手感定稿 v3",
            new DateTime(2026, 10, 13, 20, 30, 0));

        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Contains("// ===== 空鼠调参宏片段（PencilBox 导出，粘贴回 config 宏后出包固化） =====", lines);
        Assert.Contains("// 导出时间: 2026-10-13 20:30:00", lines);
        Assert.Contains("// 笔固件: MODEL=0x11  SVER=0.12.119", lines);
        Assert.Contains("// 备注: 手感定稿 v3", lines);
        Assert.Contains("#define CONFIG_AIRMOUSE_SENSITIVITY 0.35f", lines);
        Assert.Contains("#define CONFIG_AIRMOUSE_SLOW_GAIN 0.22f", lines);
        Assert.Contains("#define CONFIG_AIRMOUSE_SLOW_KNEE_DPS 10.0f", lines);
        Assert.Contains("#define CONFIG_AIRMOUSE_SLOW_WIDTH_DPS 8.0f", lines);
        Assert.Contains("#define CONFIG_AIRMOUSE_DEADZONE_DPS 0.5f", lines);
        Assert.Contains("#define CONFIG_AIRMOUSE_FREEZE_RELEASE_MS 250", lines);   // ms 不带 f
    }

    [Fact]
    public void MacroSnippet_MissingParam_CommentedNotDefine()
    {
        // 只有 3 项：缺的必须以注释行标"未读到"，不得产出半截 define 误导回填
        var readings = new List<TuningParamReading> { new(0x01, 0.35f), new(0x02, 0.22f), new(0x03, 10.0f) };

        var text = AirMouseMacroSnippet.Build(readings, null, null);

        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Contains("#define CONFIG_AIRMOUSE_SENSITIVITY 0.35f", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("#define CONFIG_AIRMOUSE_FREEZE_RELEASE_MS"));
        Assert.Contains(lines, l => l.StartsWith("// #define CONFIG_AIRMOUSE_FREEZE_RELEASE_MS"));
        Assert.Contains("未知（0x03 无应答）", text);   // 版本缺省口径（子串）
    }

    [Fact]
    public void MacroSnippet_BiasV2Items_CommentNotDefine()
    {
        // 01 卷 §5.1：零偏 v2 项不产出 #define——0x07/0x08 RAM 值、0x09/0x0A 只读镜像、0x0B 控制型，一律注释行
        var readings = new List<TuningParamReading>
        {
            new(0x07, -0.42f), new(0x08, 0.13f), new(0x09, -0.40f), new(0x0A, 0.10f), new(0x0B, 1f),
        };

        var lines = AirMouseMacroSnippet.Build(readings, "SVER=0.13.18", null)
            .Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        Assert.DoesNotContain(lines, l => l.StartsWith("#define", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("// 0x07 零偏注入 gx = -0.420") && l.Contains("RAM 运行时值"));
        Assert.Contains(lines, l => l.StartsWith("// 0x09 NVDS 零偏 gx = -0.400") && l.Contains("只读 NVDS 镜像"));
        Assert.Contains(lines, l => l.StartsWith("// 0x0B 零偏固化控制 = 1.000") && l.Contains("控制型"));
    }

    // ================= 快照存储（configs/airmouse/，A/B 互切底座） =================

    [Fact]
    public void SnapshotStore_SaveLoadDelete_Roundtrip()
    {
        var store = new AirMouseSnapshotStore(MsLogger, _tmpDir);

        Assert.Empty(store.ListNames());

        var values = new List<TuningParamReading>
        {
            new(0x01, 0.35f), new(0x02, 0.22f), new(0x03, 10.0f),
            new(0x04, 8.0f),  new(0x05, 0.5f),  new(0x06, 250f),
        };
        var saved = new AirMouseSnapshot("A", new DateTime(2026, 10, 13, 21, 0, 0),
            "第一版手感", "0.12.119", values, new[] { true, false, true, false });
        store.Save(saved);

        Assert.Equal(new[] { "A" }, store.ListNames());

        var loaded = store.Load("A");
        Assert.NotNull(loaded);
        Assert.Equal("A", loaded!.Name);
        Assert.Equal("第一版手感", loaded.Note);
        Assert.Equal("0.12.119", loaded.Firmware);
        Assert.Equal(6, loaded.Values.Count);
        Assert.Equal(0.35f, loaded.Values[0].Value, 5);
        Assert.Equal(250f, loaded.Values[5].Value, 5);
        Assert.NotNull(loaded.Checklist);
        Assert.Equal(4, loaded.Checklist!.Count);
        Assert.True(loaded.Checklist[0]);
        Assert.False(loaded.Checklist[1]);

        // 同名覆盖 + 删除
        store.Save(saved with { Note = "覆盖版" });
        Assert.Equal("覆盖版", store.Load("A")!.Note);
        Assert.True(store.Delete("A"));
        Assert.Null(store.Load("A"));
        Assert.Empty(store.ListNames());
        Assert.True(store.Delete("A"));   // 幂等
    }

    [Fact]
    public void SnapshotStore_InvalidNameChar_Sanitized()
    {
        var store = new AirMouseSnapshotStore(MsLogger, _tmpDir);
        store.Save(new AirMouseSnapshot("a/b:c", DateTime.Now, null, null,
            new List<TuningParamReading> { new(0x01, 0.3f) }));

        var names = store.ListNames();
        Assert.Single(names);
        Assert.DoesNotContain("/", names[0]);
        Assert.NotNull(store.Load("a_b_c"));
    }

    // ================= 共享哑日志（store 只在异常路径打日志） =================

    private static readonly Microsoft.Extensions.Logging.ILogger<AirMouseSnapshotStore> MsLogger =
        Microsoft.Extensions.Logging.Abstractions.NullLogger<AirMouseSnapshotStore>.Instance;
}
