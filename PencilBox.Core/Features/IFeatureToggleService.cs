// <copyright file="IFeatureToggleService.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Features;

/// <summary>功能开关变更事件（GUI 控制台/页签显隐、引擎启停的联动源）。</summary>
public sealed class FeatureToggleChangedEventArgs : EventArgs
{
    public FeatureToggleChangedEventArgs(FeatureDescriptor descriptor, bool enabled)
    {
        Descriptor = descriptor;
        Enabled = enabled;
    }

    public FeatureDescriptor Descriptor { get; }
    public bool Enabled { get; }
}

/// <summary>
/// 功能总开关服务（14 号架构提案：模块级启停 + 持久化；CLI 与 GUI 读同一开关）。
/// 语义：关 = CLI 域入口拒绝 + GUI 入口隐藏 + 该功能运行时引擎立即停；
/// 不做编译期裁剪（单 exe 简单性优先；"软触发单功能发行版"走发行级默认开关表）。
/// </summary>
public interface IFeatureToggleService
{
    /// <summary>全部已注册功能（静态目录序）。</summary>
    IReadOnlyList<FeatureDescriptor> Describe();

    /// <summary>按 id 取描述；未知 id 抛 <see cref="KeyNotFoundException"/>（拼写守卫，不静默）。</summary>
    FeatureDescriptor GetDescriptor(string featureId);

    /// <summary>是否启用；未知 id 抛 <see cref="KeyNotFoundException"/>（拼写守卫，不静默）。</summary>
    bool IsEnabled(string featureId);

    /// <summary>设置并**立即持久化**（configs/features.json）；值不变也照写照发事件（幂等无害）。</summary>
    void SetEnabled(string featureId, bool enabled);

    /// <summary>开关变更后触发（持久化成功后发；订阅方自行调度线程）。</summary>
    event EventHandler<FeatureToggleChangedEventArgs>? Changed;
}
