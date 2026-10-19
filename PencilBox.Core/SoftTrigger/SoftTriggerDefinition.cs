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

/// <summary>序列一步 = 修饰键 + 单主键（HID usage；顺序=数组序，13 号：顺序设置）。</summary>
public sealed record SoftTriggerStep(byte Usage, byte Modifier);

/// <summary>
/// 一条软触发定义（动作库键盘族，13 号；纯软件域——零设备依赖）。
/// 触发键只允许专用键 F17~F24（值域分离防回环：动作只允许普通键值，两值域不相交⇒天然无环，级联禁止）；
/// F13~F16 已被笔端工作状态自管占用（Core.Models/PenWorkState）。
/// </summary>
public sealed class SoftTriggerDefinition
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>触发键（HID usage，必须在 F17~F24 专用键域；裸键无修饰——与工作状态触发同规则）</summary>
    public byte TriggerUsageId { get; set; }

    /// <summary>回放序列（普通键值；校验见 <see cref="SoftTriggerKeys.Validate"/>）</summary>
    public List<SoftTriggerStep> Steps { get; set; } = [];

    /// <summary>步间隔 ms（序列内两步之间的间隔）</summary>
    public int StepIntervalMs { get; set; } = 60;

    public SoftTriggerRepeatMode RepeatMode { get; set; } = SoftTriggerRepeatMode.Once;

    /// <summary>Toggle 连发周期 ms</summary>
    public int RepeatIntervalMs { get; set; } = 200;

    public string Note { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
