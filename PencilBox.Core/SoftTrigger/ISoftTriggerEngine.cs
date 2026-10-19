// <copyright file="ISoftTriggerEngine.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.SoftTrigger;

/// <summary>引擎事件（环形缓冲 + 事件流；GUI 实时列表与 CLI selftest 判据共用）。</summary>
public sealed record SoftTriggerEvent(DateTime Time, string Kind, string Name, string Detail)
{
    public const string KindTriggerSeen = "TriggerSeen";
    public const string KindReplayed = "Replayed";
    public const string KindToggleStarted = "ToggleStarted";
    public const string KindToggleStopped = "ToggleStopped";
    public const string KindError = "Error";
}

/// <summary>
/// 软触发引擎（动作库键盘族 13 号；纯软件域——只依赖钩子观测腿 + 合成发腿，零设备服务）。
/// 寄宿形态：**Desktop 进程内常驻**（启动即随宿主拉起，启用定义即刻生效）；
/// CLI 是一次性进程，只在 selftest 里进程内临时拉起验证回环。
/// 防失控：总开关（IFeatureToggleService "softtrigger"）关闭即停（订阅 Changed 联动）；
/// 值域分离防回环由 <see cref="SoftTriggerKeys.Validate"/> 在存储与装载两道把关。
/// </summary>
public interface ISoftTriggerEngine
{
    /// <summary>引擎是否在跑（钩子已订阅、启用定义已装载）。</summary>
    bool IsRunning { get; }

    /// <summary>
    /// 启动（幂等）：订阅全局键盘钩子 + 装载启用定义。
    /// 总开关关闭时抛 <see cref="InvalidOperationException"/>（指路 feature on softtrigger）。
    /// </summary>
    void Start();

    /// <summary>停止（退订钩子事件 + 停全部连发定时器；**不**停共享钩子本体——工作状态监听还在用）。</summary>
    void Stop();

    /// <summary>配置变更后重载（重建启用定义缓存；连发中的按名保留）。</summary>
    void Reload();

    /// <summary>手动试射：不经钩子直接回放指定定义（CLI `run` / GUI 试射按钮；定义禁用时拒发）。</summary>
    void Fire(string name);

    /// <summary>最近事件（新在前，截取 count 条）。</summary>
    IReadOnlyList<SoftTriggerEvent> RecentEvents(int count);

    /// <summary>事件流（钩子/定时器线程触发，订阅方自行调度）。</summary>
    event EventHandler<SoftTriggerEvent>? EventLogged;
}
