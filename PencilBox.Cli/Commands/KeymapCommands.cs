// <copyright file="KeymapCommands.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using Microsoft.Extensions.Logging;
using PencilBox.Cli.Output;
using PencilBox.Core.Fsm;
using PencilBox.Core.Models;
using PencilBox.Core.Protocol;
using PencilBox.Core.Services;

namespace PencilBox.Cli.Commands;

/// <summary>
/// `keymap` 域——命名 Keymap 配置的本地管理（pc-issues/04 提案 A/B 的 CLI-first 落地，2026-10-08）。
/// 本地文件操作（`&lt;exe 同级&gt;/configs/keymap/`）；`apply` 子命令走 `0x55` 整表下发基站
/// （回执门控：下发成功才切上位机当前配置，2026-10-18 定版）。
/// 存储层保存/加载即按 K1→K6 规范排序（<see cref="KeymapSorting"/>），故写后索引可能与命令中给出的不同。
/// </summary>
public sealed class KeymapCommands
{
    private readonly IKeymapConfigStore _store;
    private readonly CompanyKeymapConfigService _keymapConfig;
    private readonly ILogger<KeymapCommands> _logger;

    public KeymapCommands(IKeymapConfigStore store, CompanyKeymapConfigService keymapConfig, ILogger<KeymapCommands> logger)
    {
        _store = store;
        _keymapConfig = keymapConfig;
        _logger = logger;
    }

    /// <summary>`keymap list`：配置清单（含当前标记与只读标注）</summary>
    public int List()
    {
        _store.Initialize();
        var current = _store.GetCurrentName();
        foreach (var name in _store.ListNames())
        {
            var doc = _store.Load(name);
            var marks = string.Equals(name, current, StringComparison.Ordinal) ? " ← 当前" : "";
            if (string.Equals(name, _store.FactoryName, StringComparison.Ordinal))
                marks += "（只读，出厂默认）";
            ConsoleFormatter.Info($"  {name}{marks}　{doc?.Entries.Count ?? 0} 条");
        }
        return 0;
    }

    /// <summary>`keymap show [配置]`：表格展示（缺省 = 当前配置）</summary>
    public int Show(string? name)
    {
        _store.Initialize();
        name = Resolve(name);
        var doc = _store.Load(name);
        if (doc == null)
        {
            ConsoleFormatter.Error($"配置不存在或不可读：{name}");
            return 1;
        }

        ConsoleFormatter.Info($"配置「{name}」（{doc.Entries.Count} 条{(IsFactory(name) ? "，只读" : "")}）");
        ConsoleFormatter.PrintTable(
            doc.Entries.Select((e, i) => (Entry: e, Index: (byte)i)),
            ("#", r => r.Index.ToString()),
            ("按键", r => KeymapFormatter.GetKeyName(r.Entry.KeyId)),
            ("事件", r => KeymapFormatter.GetEventDisplay(r.Entry.KeyId, r.Entry.EventType)),
            ("动作", r => KeymapFormatter.GetActionTypeName(r.Entry.ActionType)),
            ("码值", r => KeymapFormatter.GetCodeName(r.Entry.ActionType, r.Entry.CodeLo, r.Entry.CodeHi)),
            ("修饰键", r => KeymapFormatter.GetModifierName(r.Entry.Modifier)),
            ("RAW", r => string.Join(" ", r.Entry.ToBytes().Select(b => $"{b:X2}"))));

        // S1 档位切换快捷键（固件另一张表 s_switch_default：档位→热键；随配置携带，未携带回落出厂真值；
        // 设备写回待 0x55 PARAM 05/06，pc-issues/05）
        var switchEntries = doc.SwitchEntries ?? KeymapFactoryDefault.SwitchEntries.ToList();
        ConsoleFormatter.Info(
            $"S1 档位切换快捷键（切换表{(doc.SwitchEntries == null ? "，本配置未携带→显示出厂真值" : "")}；设备写回待 0x55，pc-issues/05）");
        ConsoleFormatter.PrintTable(
            switchEntries
                .Select((e, i) => (Entry: e, Index: (byte)i))
                .OrderBy(r => r.Entry.Mode),
            ("#", r => r.Index.ToString()),
            ("档位", r => KeymapFormatter.GetModeName(r.Entry.Mode)),
            ("动作", r => KeymapFormatter.GetActionTypeName(KeyActionType.Keyboard)),
            ("码值", r => KeymapFormatter.GetCodeName(KeyActionType.Keyboard, r.Entry.CodeLo, 0)),
            ("修饰键", r => KeymapFormatter.GetModifierName(r.Entry.Modifier)),
            ("RAW", r => string.Join(" ", r.Entry.ToBytes().Select(b => $"{b:X2}"))));
        return 0;
    }

    /// <summary>`keymap new &lt;名&gt; [--from &lt;配置&gt;|factory]`：复制创建并设为当前（缺省源 = 出厂默认）</summary>
    public int New(string name, string? from)
    {
        _store.Initialize();
        from ??= _store.FactoryName;
        if (from.Equals("factory", StringComparison.OrdinalIgnoreCase))
            from = _store.FactoryName;   // 帮助文本承诺的别名（--from factory），2026-10-17 冒烟发现从未实现，补上
        var src = _store.Load(from);
        if (src == null)
        {
            ConsoleFormatter.Error($"源配置不存在：{from}");
            return 1;
        }

        _store.Save(name, src.Entries, src.SwitchEntries);
        _store.SetCurrentName(name);
        ConsoleFormatter.Success($"已创建配置「{name}」（复制自「{from}」，{src.Entries.Count} 条）并设为当前");
        return 0;
    }

    /// <summary>
    /// `keymap set &lt;名&gt; --index N [--key K3] [--event click] [--action keyboard] [--code 0x4B] [--code-hi 0] [--mod alt]`：
    /// 修改第 N 条（`--index` = 条目数表示追加）。写后存储按 K1→K6 重排，索引可能变化。
    /// </summary>
    public int Set(string name, int index, byte? keyId, KeyEventType? eventType, KeyActionType? actionType,
        byte? code, byte? codeHi, byte? modifier)
    {
        _store.Initialize();
        var (doc, error) = LoadMutable(name);
        if (doc == null)
        {
            ConsoleFormatter.Error(error!);
            return 1;
        }
        if (index < 0 || index > doc.Entries.Count)
        {
            ConsoleFormatter.Error($"--index {index} 越界（可用 0~{doc.Entries.Count}，=条目数表示追加）");
            return 1;
        }

        var entry = index == doc.Entries.Count ? new KeymapEntry() : doc.Entries[index];
        if (keyId.HasValue) entry.KeyId = keyId.Value;
        if (eventType.HasValue) entry.EventType = eventType.Value;
        if (actionType.HasValue) entry.ActionType = actionType.Value;
        if (code.HasValue) entry.CodeLo = code.Value;
        if (codeHi.HasValue) entry.CodeHi = codeHi.Value;
        if (modifier.HasValue) entry.Modifier = modifier.Value;

        if (KeymapRules.IsK2EventViolation(entry.KeyId, entry.EventType))
        {
            ConsoleFormatter.Error(
                $"拒绝写入：K2 行事件只允许 会话开始(long)/会话结束(longrel)。{KeymapRules.K2EventLockReason}");
            return 1;
        }

        if (index == doc.Entries.Count)
            doc.Entries.Add(entry);
        _store.Save(name, doc.Entries);   // Save 内部按规范序重排

        ConsoleFormatter.Success(
            $"已写入「{name}」：{KeymapFormatter.GetKeyName(entry.KeyId)} {KeymapFormatter.GetEventDisplay(entry.KeyId, entry.EventType)} → " +
            $"{KeymapFormatter.GetActionTypeName(entry.ActionType)} {KeymapFormatter.GetCodeName(entry.ActionType, entry.CodeLo, entry.CodeHi)}" +
            $"（存储已按 K1→K6 重排，用 show 核对索引）");
        return 0;
    }

    /// <summary>
    /// `keymap switch &lt;名&gt; --mode 0|1|2 [--code 0x6A] [--mod alt]`：修改 S1 切换表某档位热键
    /// （三档行固定不可增删；配置未携带切换表时先落出厂真值再改——首次编辑即把表写入配置文件）。
    /// </summary>
    public int Switch(string name, byte mode, byte? code, byte? modifier)
    {
        _store.Initialize();
        if (mode > 2)
        {
            ConsoleFormatter.Error($"--mode {mode} 越界（1=ON 2=SPK；0=IDLE 内部行不可改）");
            return 1;
        }
        if (KeymapRules.IsSwitchInternalRow(mode))
        {
            ConsoleFormatter.Error($"拒绝写入：{KeymapRules.SwitchIdleLockReason}");
            return 1;
        }
        if (!code.HasValue && !modifier.HasValue)
        {
            ConsoleFormatter.Error("至少给一个字段：--code <byte> / --mod <ctrl|shift|alt|win|none|0~255>（用 show 看当前值）");
            return 1;
        }

        var (doc, error) = LoadMutable(name);
        if (doc == null)
        {
            ConsoleFormatter.Error(error!);
            return 1;
        }

        // 出厂真值兜底必须**克隆条目**——常量数组对象是共享锚点，绝不原地改
        var list = doc.SwitchEntries ?? KeymapFactoryDefault.SwitchEntries
            .Select(e => new KeymapSwitchEntry { Mode = e.Mode, Modifier = e.Modifier, CodeLo = e.CodeLo })
            .ToList();
        var entry = list.FirstOrDefault(e => e.Mode == mode);
        if (entry == null)
        {
            entry = new KeymapSwitchEntry { Mode = mode };
            list.Add(entry);
        }
        if (code.HasValue) entry.CodeLo = code.Value;
        if (modifier.HasValue) entry.Modifier = modifier.Value;

        doc.SwitchEntries = list;
        _store.Save(name, doc.Entries, list);

        ConsoleFormatter.Success(
            $"已写入「{name}」S1 切换表：{KeymapFormatter.GetModeName(mode)} → " +
            $"{KeymapFormatter.GetCodeName(KeyActionType.Keyboard, entry.CodeLo, 0)}" +
            (entry.Modifier != 0 ? $" +{KeymapFormatter.GetModifierName(entry.Modifier)}" : "") +
            "（RAW " + string.Join(" ", entry.ToBytes().Select(b => $"{b:X2}")) + "）");
        return 0;
    }

    /// <summary>`keymap remove &lt;名&gt; --index N`：删除一条</summary>
    public int Remove(string name, int index)
    {
        _store.Initialize();
        var (doc, error) = LoadMutable(name);
        if (doc == null)
        {
            ConsoleFormatter.Error(error!);
            return 1;
        }
        if (index < 0 || index >= doc.Entries.Count)
        {
            ConsoleFormatter.Error($"--index {index} 越界（0~{doc.Entries.Count - 1}）");
            return 1;
        }

        doc.Entries.RemoveAt(index);
        _store.Save(name, doc.Entries);
        ConsoleFormatter.Success($"已删除「{name}」第 {index} 条（剩 {doc.Entries.Count} 条；已按 K1→K6 重排）");
        return 0;
    }

    /// <summary>`keymap rename &lt;旧&gt; &lt;新&gt;`</summary>
    public int Rename(string oldName, string newName)
    {
        _store.Initialize();
        try
        {
            _store.Rename(oldName, newName);
            ConsoleFormatter.Success($"已改名「{oldName}」→「{newName}」");
            return 0;
        }
        catch (Exception ex)
        {
            ConsoleFormatter.Error($"改名失败：{ex.Message}");
            return 1;
        }
    }

    /// <summary>`keymap delete &lt;名&gt;`（出厂默认拒绝；不存在报错不静默）</summary>
    public int Delete(string name)
    {
        _store.Initialize();
        try
        {
            if (_store.Load(name) == null)
            {
                ConsoleFormatter.Error($"配置不存在：{name}");
                return 1;
            }
            _store.Delete(name);
            ConsoleFormatter.Success($"已删除配置「{name}」（当前配置回落：{_store.GetCurrentName()}）");
            return 0;
        }
        catch (Exception ex)
        {
            ConsoleFormatter.Error($"删除失败：{ex.Message}");
            return 1;
        }
    }

    /// <summary>`keymap use &lt;名&gt;`：设为当前配置（仪表盘启动载入的就是它）</summary>
    public int Use(string name)
    {
        _store.Initialize();
        if (_store.Load(name) == null)
        {
            ConsoleFormatter.Error($"配置不存在：{name}");
            return 1;
        }
        _store.SetCurrentName(name);
        ConsoleFormatter.Success($"当前配置已切换为「{name}」");
        return 0;
    }

    /// <summary>
    /// `keymap apply [配置]`：`0x55` **整表下发**基站（探测→逐条写 RAM→切换表→提交 NVDS，两段式）。
    /// 回执门控（2026-10-18 定版）：基站全链回执成功才把上位机当前配置切过去；未连接/未响应/中途失败一律保持原状。
    /// 整表重发是有意的：设备表现状未知（可能已被其他表/来源改过），免对账；增量等 0x55 读侧落地后再议。
    /// 出厂默认也可 apply——语义=恢复设备默认表。
    /// </summary>
    public async Task<int> Apply(string? name)
    {
        _store.Initialize();
        name = Resolve(name);
        var doc = _store.Load(name);
        if (doc == null)
        {
            ConsoleFormatter.Error($"配置不存在或不可读：{name}");
            return 1;
        }
        if (!_keymapConfig.IsOpen)
        {
            ConsoleFormatter.Error("未连接基站（USB 命令通道）——生效需先连接接收器（GUI 连接或 `device connect`）");
            return 1;
        }

        var switchEntries = doc.SwitchEntries ?? KeymapFactoryDefault.SwitchEntries.ToList();
        var userSwitchCount = switchEntries.Count(e => !KeymapRules.IsSwitchInternalRow(e.Mode));
        ConsoleFormatter.Info($"正在下发「{name}」：全表 {doc.Entries.Count} 条 + 切换表 {userSwitchCount} 行" +
                               "（0x55 两段式：逐条写 RAM → 提交 NVDS）…");

        var report = await _keymapConfig.ApplyAsync(doc.Entries, switchEntries);
        if (report.AllOk)
        {
            _store.SetCurrentName(name);
            ConsoleFormatter.Success($"已下发并提交（{report.Written} 条）；上位机当前生效配置已切换为「{name}」" +
                                     "（识别/扩音触发键=K2/S1 行随之生效）");
            return 0;
        }
        if (!report.ProbeResponded)
            ConsoleFormatter.Error("基站未响应（0x55 未开通/旧固件）——未下发成功，上位机保持原状");
        else
            ConsoleFormatter.Error($"{report.Error}——未下发成功，上位机保持原状");
        return 1;
    }

    // ===== 解析辅助已归位 Core：KeymapTextParser（CLI/测试共用，与 KeymapFormatter 成对） =====

    // ===== 内部 =====

    private bool IsFactory(string name) =>
        string.Equals(name, _store.FactoryName, StringComparison.Ordinal);

    private string Resolve(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
            return name;
        _store.Initialize();
        return _store.GetCurrentName();
    }

    /// <summary>加载可编辑（存在且非出厂默认）的配置。</summary>
    private (KeymapConfigDocument? Doc, string? Error) LoadMutable(string name)
    {
        name = Resolve(name);
        if (IsFactory(name))
            return (null, "出厂默认只读——先 `keymap new <名> [--from factory]` 复制后修改");
        var doc = _store.Load(name);
        return doc == null ? (null, $"配置不存在或不可读：{name}") : (doc, null);
    }
}
