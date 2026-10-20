// <copyright file="ChargeTimingTracker.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Protocol.Company;

namespace PencilBox.Core.Models;

/// <summary>
/// 充电/放电计时器（指挥官 2026-10-20 需求：两个时间位全程利用 + 双充电口径对比选型）。
///
/// 纯逻辑、观测驱动：调用方把每一条**真实观测**（0x53 快照 / 0x42 应答的充电态+电量）
/// 按时间序喂入；两次观测之间墙钟自行走表——**断连不清表**（笔待机断连是常态，
/// "离电运行时长"须以真实时间计，重连后按最新观测推进状态机）。
///
/// 两个计时位（显示"最近一个周期"，进程重启即清零）：
///   ① **充电位**：非充电→充电中起表；电量显示到 100% 停「显示口径」表；
///      PMU 充满（byte2=2，芯片寄存器 FINISH 判据）停「PMU 口径」表——两口径并排输出
///      供产品选型（显示口径通常偏早，PMU 口径含恒压涓流补饱和段）；未充满拔出=中止。
///   ② **放电位**：充电路径终结（充满/100%/中止）转入非充电那一刻起表，
///      到下一次进入充电态定格 = 产品一次离电运行的时长。
///
/// 判据边界：未知电量（0xFF/无槽 → null）不参与 100% 判定，只随状态转移；
/// **笔未连接 ≠ 未充电**（0x53 STATUS=02 / 断连是"未知"，调用方不得喂 0）。
/// </summary>
public sealed class ChargeTimingTracker
{
    private bool _hasCycle;                     // 本进程内是否出现过充电周期（"无数据 —"与真实周期的区分）
    private bool _charging;                     // 在充电路径上（含充满未拔出）
    private bool _fullLatched;                  // 已观测到 PMU 充满
    private DateTimeOffset? _chargeStart;       // 周期起点（null=起点未观测到，如满电态重连）
    private byte? _startPercent;
    private TimeSpan? _toDisplayFull;           // 显示口径：起表→电量 100%
    private TimeSpan? _toPmuFull;               // PMU 口径：起表→寄存器充满
    private bool _chargeAborted;                // 本周期未充满即拔出
    private TimeSpan? _finalChargeElapsed;      // 周期结束时刻的总充电时长（中止时=拔出时刻-起点）
    private DateTimeOffset? _dischargeStart;
    private DateTimeOffset? _dischargeEnd;
    private string? _dischargeOrigin;

    /// <summary>是否有走表中的计时（文本会随墙钟变化，调用方据此决定要不要刷新）——
    /// 充满未拔出时两表都已定格，不算走表。</summary>
    public bool IsTicking => (_charging && !_fullLatched && _chargeStart != null) ||
                             (_dischargeStart != null && _dischargeEnd == null);

    /// <summary>喂一条观测（按时间序；重复/乱序值由状态机幂等消化）。<paramref name="batteryPercent"/>=null 表示未知电量。</summary>
    public void Feed(DateTimeOffset now, byte chargeState, byte? batteryPercent)
    {
        switch (chargeState)
        {
            case PenChargeState.Charging:
                if (!_charging)
                    BeginCharge(now, batteryPercent, fullLatched: false);
                else if (batteryPercent >= 100 && _toDisplayFull == null && _chargeStart != null)
                    _toDisplayFull = now - _chargeStart.Value;   // 首次到达即定格，重复 100% 不改写
                break;

            case PenChargeState.Full:
                if (!_charging)
                    BeginCharge(now, batteryPercent, fullLatched: true);   // 满电态重连：充电起点未观测
                else if (!_fullLatched)
                {
                    _fullLatched = true;
                    if (_chargeStart != null)
                        _toPmuFull = now - _chargeStart.Value;   // PMU FINISH 真判据
                }
                break;

            case PenChargeState.None:
                if (_charging)
                    EndCharge(now);
                break;

            // 0x03 保留值/未知值：不推进状态机（前向兼容，勿误判为未充电）
        }
    }

    /// <summary>当前计时快照（最近一个周期；无数据字段为 null）。</summary>
    public ChargeTimingReport Report(DateTimeOffset now) => new(
        Charging: _charging,
        StartPercent: _startPercent,
        ToDisplayFull: _toDisplayFull,
        ToPmuFull: _toPmuFull,
        ChargeAborted: _chargeAborted,
        ChargeElapsed: _charging && _chargeStart != null ? now - _chargeStart : _finalChargeElapsed,
        DischargeElapsed: _dischargeStart == null ? null : (_dischargeEnd ?? now) - _dischargeStart,
        DischargeOrigin: _dischargeOrigin,
        DischargeTicking: _dischargeStart != null && _dischargeEnd == null,
        HasCycle: _hasCycle);

    private void BeginCharge(DateTimeOffset now, byte? startPercent, bool fullLatched)
    {
        if (_dischargeStart != null && _dischargeEnd == null)
            _dischargeEnd = now;   // 放电表定格：本次离电运行到此为止

        _hasCycle = true;
        _charging = true;
        _fullLatched = fullLatched;
        _chargeStart = fullLatched ? null : now;
        _startPercent = fullLatched ? null : startPercent;
        _toDisplayFull = null;
        _toPmuFull = null;
        _chargeAborted = false;
        _finalChargeElapsed = null;
    }

    private void EndCharge(DateTimeOffset now)
    {
        _charging = false;
        if (_chargeStart != null)
            _finalChargeElapsed = now - _chargeStart;
        if (!_fullLatched && _toDisplayFull == null)
            _chargeAborted = true;

        _dischargeStart = now;
        _dischargeEnd = null;
        _dischargeOrigin = _fullLatched ? "自充满取下"
            : _toDisplayFull != null ? "自100%拔出"
            : "未充满拔出";
    }
}

/// <summary>计时快照（<see cref="ChargeTimingTracker.Report"/>）。</summary>
public sealed record ChargeTimingReport(
    bool Charging,
    byte? StartPercent,
    TimeSpan? ToDisplayFull,
    TimeSpan? ToPmuFull,
    bool ChargeAborted,
    TimeSpan? ChargeElapsed,
    TimeSpan? DischargeElapsed,
    string? DischargeOrigin,
    bool DischargeTicking,
    bool HasCycle);

/// <summary>计时文本格式化（CLI/GUI 单一来源）。</summary>
public static class ChargeTimingFormat
{
    /// <summary>充电位：双口径并排（产品选型对比用）。计中=尚未到任何口径/未充满拔出前。</summary>
    public static string ChargeText(ChargeTimingReport r)
    {
        if (!r.HasCycle)
            return "充电 —";

        var since = r.StartPercent is { } p ? $"，自 {p}% 起" : "";

        if (r.ToDisplayFull == null && r.ToPmuFull == null && r.ChargeElapsed == null)
            return r.Charging ? "充电 —（计中，起点未观测）" : "充电 —（起点未观测，自满电态接续）";

        if (r.ToDisplayFull == null && r.ToPmuFull == null)
            return r.Charging
                ? $"充电 {Duration(r.ChargeElapsed)}（计中{since}）"
                : $"充电 {Duration(r.ChargeElapsed)}（中止·未充满）";

        // 双口径至少一个已定格（可能仍插着等另一口径）
        var head = $"充电 到100% {Fixed(r.ToDisplayFull)}; PMU充满 {Fixed(r.ToPmuFull)}";
        var tail = r.Charging ? "" : r.ChargeElapsed == null ? "" : $"（总 {Duration(r.ChargeElapsed)}）";
        return head + tail;
    }

    /// <summary>放电位：一次离电运行时长。</summary>
    public static string DischargeText(ChargeTimingReport r) =>
        r.DischargeOrigin == null ? "放电 —"
        : $"放电 {Duration(r.DischargeElapsed)}（{r.DischargeOrigin}{(r.DischargeTicking ? "·计中" : "")}）";

    /// <summary>时长显示：≥1h 用小时两位小数，否则整分钟。</summary>
    public static string Duration(TimeSpan? t) => t == null ? "—"
        : t.Value.TotalHours >= 1 ? $"{t.Value.TotalHours:0.##} h"
        : $"{t.Value.TotalMinutes:0} 分";

    private static string Fixed(TimeSpan? t) => t == null ? "—" : Duration(t);
}
