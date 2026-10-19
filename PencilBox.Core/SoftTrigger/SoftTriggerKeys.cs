// <copyright file="SoftTriggerKeys.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.SoftTrigger;

/// <summary>
/// 软触发值域与校验（13 号防失控三律之一：值域分离）。
/// 触发键域=F17~F24（0x6C~0x73，专用键、物理键盘不存在、系统零冲突）；
/// 动作（序列步）只允许普通键值——F13~F24 专用键段整体禁入（F17~F24 防自触发回环；
/// F13~F16 是笔端工作状态触发键，回放会误扰工作状态两轴）。
/// </summary>
public static class SoftTriggerKeys
{
    /// <summary>触发键域下界：F17 = 0x6C</summary>
    public const byte TriggerLo = 0x6C;

    /// <summary>触发键域上界：F24 = 0x73</summary>
    public const byte TriggerHi = 0x73;

    /// <summary>专用键保留段下界：F13 = 0x68（动作禁入）</summary>
    public const byte ReservedLo = 0x68;

    /// <summary>专用键保留段上界：F24 = 0x73（动作禁入）</summary>
    public const byte ReservedHi = 0x73;

    public static bool IsTriggerDomain(byte usage) => usage is >= TriggerLo and <= TriggerHi;

    /// <summary>专用键段（F13~F24）——动作禁入</summary>
    public static bool IsReservedRange(byte usage) => usage is >= ReservedLo and <= ReservedHi;

    /// <summary>usage → 键名（触发键域内）："F17"…；域外抛异常。</summary>
    public static string TriggerName(byte usage) =>
        IsTriggerDomain(usage) ? $"F{usage - TriggerLo + 17}" : throw new ArgumentException($"usage 0x{usage:X2} 不在触发键域（F17~F24）");

    /// <summary>键名 → usage（触发键域内）；非法抛异常。</summary>
    public static byte ParseTriggerName(string name)
    {
        var t = name.Trim().ToUpperInvariant();
        if (t.Length >= 2 && t[0] == 'F' && int.TryParse(t[1..], out var f) && f is >= 17 and <= 24)
            return (byte)(TriggerLo + f - 17);
        throw new ArgumentException($"触发键必须是 F17~F24（专用键域，防回环）：得到「{name}」");
    }

    /// <summary>定义合法性校验（存储与引擎装载共用一关）；违规抛 <see cref="ArgumentException"/>。</summary>
    public static void Validate(SoftTriggerDefinition def)
    {
        if (string.IsNullOrWhiteSpace(def.Name))
            throw new ArgumentException("软触发名不能为空");
        if (!IsTriggerDomain(def.TriggerUsageId))
            throw new ArgumentException($"触发键 0x{def.TriggerUsageId:X2} 不在专用键域——只允许 F17~F24（值域分离防回环，13 号）");
        if (def.Steps.Count == 0)
            throw new ArgumentException("回放序列为空——至少一步普通键值");
        foreach (var step in def.Steps)
        {
            if (step.Usage == 0)
                throw new ArgumentException($"序列含空键值（0x00）——「{def.Name}」");
            if (IsReservedRange(step.Usage))
                throw new ArgumentException(
                    $"序列步 0x{step.Usage:X2}（F{step.Usage - ReservedLo + 13}）属专用键保留段，禁止作动作——" +
                    "F17~F24 防软触发回环，F13~F16 属工作状态触发键（13 号值域分离律）");
        }
        if (def.StepIntervalMs is < 0 or > 5000)
            throw new ArgumentException($"步间隔越界（0~5000ms）：{def.StepIntervalMs}");
        if (def.RepeatMode == SoftTriggerRepeatMode.Toggle && def.RepeatIntervalMs is < 20 or > 60_000)
            throw new ArgumentException($"连发周期越界（20~60000ms）：{def.RepeatIntervalMs}");
    }
}
