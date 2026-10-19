// <copyright file="SoftTriggerKeys.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.SoftTrigger;

/// <summary>
/// 软触发值域与校验（13 号防失控律）。
///
/// v2（指挥官 2026-10-19 目验反馈"软触发一定要足够自由"）：
/// 触发键池=**全键盘可映射键 ± 修饰组合**（F13~F16 除外——笔端工作状态自管保留段，回放会误扰两轴）；
/// 动作（序列步）=可映射键，允许 F17~F24（动态预留律管着：只要没有启用定义把它当触发键，回放无害）；
/// 防回环升级为**动态预留全局律**（<see cref="ValidateGlobal"/>）：
/// 所有启用定义的触发键（usage+modifier 精确对）∉ 任何启用定义的动作序列——
/// 触发集合 ∩ 动作集合 = ∅ ⇒ 按触发键只会命中触发、回放永不触发，天然无环、级联禁止。
/// </summary>
public static class SoftTriggerKeys
{
    /// <summary>工作状态自管保留段：F13~F16（0x68~0x6B）——触发键与动作双双禁入</summary>
    public const byte WorkStateLo = 0x68;
    public const byte WorkStateHi = 0x6B;

    /// <summary>步数上限（防超长编排）</summary>
    public const int MaxSteps = 32;

    /// <summary>步前延时上限 ms</summary>
    public const int MaxStepDelayMs = 60_000;

    /// <summary>可映射键域（与 KeyboardUsageMap / SyntheticKeyboardSender 互逆表同源的字节字典）</summary>
    public static bool IsMappableUsage(byte usage) =>
        usage is >= 0x04 and <= 0x27                                    // A~Z / 0~9
            or >= 0x3A and <= 0x45                                      // F1~F12
            or >= 0x68 and <= 0x73                                      // F13~F24
            or 0x28 or 0x29 or 0x2A or 0x2B or 0x2C                     // Enter Esc Backspace Tab Space
            or 0x49 or 0x4A or 0x4B or 0x4C or 0x4D or 0x4E             // Ins Home PgUp Del End PgDn
            or 0x4F or 0x50 or 0x51 or 0x52;                            // → ↓ ↑ ←

    /// <summary>触发键候选=可映射且不在工作状态保留段（v2 全键盘池）。</summary>
    public static bool IsTriggerCandidate(byte usage) => IsMappableUsage(usage) && usage is < WorkStateLo or > WorkStateHi;

    /// <summary>工作状态保留段（F13~F16）——触发/动作双双禁入。</summary>
    public static bool IsWorkStateReserved(byte usage) => usage is >= WorkStateLo and <= WorkStateHi;

    /// <summary>修饰位合法（0x0F 内的位和：0x01=Ctrl 0x02=Shift 0x04=Alt 0x08=Win）。</summary>
    public static bool IsValidModifier(byte modifier) => (modifier & ~0x0F) == 0;

    /// <summary>usage → 键名（与 SoftTriggerDescriber.MainKeyName 同一张表的快捷面；不可映射出 hex）。</summary>
    public static string UsageName(byte usage) => Models.SoftTriggerDescriber.MainKeyName(usage);

    /// <summary>定义合法性校验（单定义部分；存储与引擎装载共用一关）；违规抛 <see cref="ArgumentException"/>。</summary>
    public static void Validate(SoftTriggerDefinition def)
    {
        if (string.IsNullOrWhiteSpace(def.Name))
            throw new ArgumentException("软触发名不能为空");
        if (!IsTriggerCandidate(def.TriggerUsageId))
            throw new ArgumentException(
                IsWorkStateReserved(def.TriggerUsageId)
                    ? $"触发键 0x{def.TriggerUsageId:X2}（F{def.TriggerUsageId - WorkStateLo + 13}）属工作状态保留段（F13~F16），不可作软触发"
                    : $"触发键 0x{def.TriggerUsageId:X2} 不可用——须为可映射键盘键（F13~F16 工作状态保留除外）");
        if (!IsValidModifier(def.TriggerModifier))
            throw new ArgumentException($"触发修饰位非法：0x{def.TriggerModifier:X2}（可用 ctrl/shift/alt/win 位和）");
        if (def.Steps.Count == 0)
            throw new ArgumentException("回放序列为空——至少一步");
        if (def.Steps.Count > MaxSteps)
            throw new ArgumentException($"序列步数超上限（≤{MaxSteps}）：{def.Steps.Count}");
        foreach (var step in def.Steps)
        {
            if (step.Usage == 0)
                throw new ArgumentException($"序列含空键值（0x00）——「{def.Name}」");
            if (!IsMappableUsage(step.Usage))
                throw new ArgumentException($"序列步 0x{step.Usage:X2} 不是可映射键盘键——「{def.Name}」");
            if (IsWorkStateReserved(step.Usage))
                throw new ArgumentException(
                    $"序列步 0x{step.Usage:X2}（F{step.Usage - WorkStateLo + 13}）属工作状态保留段（F13~F16），禁止作动作——「{def.Name}」");
            if (step.Usage == def.TriggerUsageId && step.Modifier == def.TriggerModifier)
                throw new ArgumentException(
                    $"动作含本定义触发键 {Models.SoftTriggerDescriber.DescribeStep(new SoftTriggerStep(def.TriggerUsageId, def.TriggerModifier))}——按下即自触发回环——「{def.Name}」");
            if (!IsValidModifier(step.Modifier))
                throw new ArgumentException($"序列步修饰位非法：0x{step.Modifier:X2}——「{def.Name}」");
            if (step.DelayMsBefore is < 0 or > MaxStepDelayMs)
                throw new ArgumentException($"步前延时越界（0~{MaxStepDelayMs}ms）：{step.DelayMsBefore}ms——「{def.Name}」");
        }
        if (def.RepeatMode == SoftTriggerRepeatMode.Toggle && def.RepeatIntervalMs is < 20 or > 60_000)
            throw new ArgumentException($"连发周期越界（20~60000ms）：{def.RepeatIntervalMs}");
    }

    /// <summary>
    /// **动态预留全局律**（v2 防回环核心）：启用集内，触发键（usage+modifier 精确对）集合
    /// 与动作步（usage+modifier）集合交集必须为空。保存/启用时与引擎装载兜底共用。
    /// 违规抛 <see cref="ArgumentException"/> 并点名冲突双方。
    /// </summary>
    public static void ValidateGlobal(IReadOnlyList<SoftTriggerDefinition> enabledDefs)
    {
        var triggers = new HashSet<(byte Usage, byte Modifier)>();
        foreach (var def in enabledDefs)
            triggers.Add((def.TriggerUsageId, def.TriggerModifier));

        foreach (var def in enabledDefs)
        {
            foreach (var step in def.Steps)
            {
                if (triggers.Contains((step.Usage, step.Modifier)))
                {
                    var owner = enabledDefs.First(d => d.TriggerUsageId == step.Usage && d.TriggerModifier == step.Modifier);
                    throw new ArgumentException(
                        $"回环拦截：「{def.Name}」的动作 {Models.SoftTriggerDescriber.DescribeStep(step)} 是「{owner.Name}」的触发键——" +
                        "启用集内触发键与动作键不得相交（动态预留律，13 号 v2）");
                }
            }
        }
    }
}
