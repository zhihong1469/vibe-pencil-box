// <copyright file="SoftTriggerDefinition.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.SoftTrigger;

/// <summary>连发模式（13 号：周期设置）。</summary>
public enum SoftTriggerRepeatMode
{
    /// <summary>单次：触发键每按一次回放一遍序列。</summary>
    Once,

    /// <summary>开关连发：触发键按下开始按周期回放，再按一次停止（经典连点语义）。</summary>
    Toggle,
}

/// <summary>
/// 序列一步 = 修饰键 + 单主键 + 步前延时（HID usage；顺序=数组序，13 号：顺序/周期自由编排）。
/// DelayMsBefore=本步回放【前】等待的毫秒数（0=立即）——周期穿插在步间；
/// 触发源后的第一步设 200~300ms 可避免回放键与触发键按压交错（v2.3，指挥官 F2→Shift+Alt+L
/// 回放夹进 F2 按压导致目标软件热键判定经常失效的教训；引擎另有"等触发键松开再回放"双保险）。
/// </summary>
public sealed record SoftTriggerStep(byte Usage, byte Modifier, int DelayMsBefore = 0);

/// <summary>
/// 一条软触发定义（动作库键盘族，13 号；纯软件域——零设备依赖）。
///
/// v2（指挥官 2026-10-19 目验反馈）：触发键放开到全键盘池（"软触发一定要足够自由"）——
/// 任意可映射键 ± 修饰组合皆可做触发键（F13~F16 除外=工作状态保留）；
/// 防回环由"固定值域分离"升级为**动态预留全局律**：所有启用定义的触发键（键+修饰精确对）
/// 不得出现在任何启用定义的动作序列里（<see cref="SoftTriggerKeys.ValidateGlobal"/>），
/// 两集合不相交⇒天然无环，级联禁止。
/// </summary>
public sealed class SoftTriggerDefinition
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>触发键（HID usage；全键盘池——可映射键、F13~F16 工作状态保留段除外）</summary>
    public byte TriggerUsageId { get; set; }

    /// <summary>触发修饰组合（0x01=Ctrl 0x02=Shift 0x04=Alt 0x08=Win 位和；钩子侧精确匹配防误触）</summary>
    public byte TriggerModifier { get; set; }

    /// <summary>回放序列（步=修饰+主键+步前延时）</summary>
    public List<SoftTriggerStep> Steps { get; set; } = [];

    /// <summary>同触发键命中多条时的执行优先级（大者先回放）</summary>
    public int Priority { get; set; }

    /// <summary>[旧版兼容] v1 全局步间隔 ms；v2 起逐步延时（DelayMsBefore），装载时摊给未显式设延时的步</summary>
    public int StepIntervalMs { get; set; }

    public SoftTriggerRepeatMode RepeatMode { get; set; } = SoftTriggerRepeatMode.Once;

    /// <summary>Toggle 连发周期 ms</summary>
    public int RepeatIntervalMs { get; set; } = 200;

    public string Note { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
