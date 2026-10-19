// <copyright file="FeatureDescriptor.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Features;

/// <summary>
/// 可选功能模块描述（14 号架构提案：功能模块化与总开关，2026-10-19）。
/// CLI 与 GUI 消费同一份静态目录（<see cref="FeatureCatalog"/>）——模块清单不依赖运行时 DI，
/// 头less 的 CLI 也能枚举与门禁同一批功能。
/// </summary>
/// <param name="Id">稳定标识（features.json 键名；小写短横线，如 <c>airmouse</c>）</param>
/// <param name="Name">展示名（控制台一行一条）</param>
/// <param name="Description">一句话说明（关掉影响什么）</param>
/// <param name="DefaultEnabled">发行级默认值（产品版默认开关表在此定；缺省全开=老用户零行为变化）</param>
/// <param name="CliDomains">管辖的 CLI 域（门禁提示用，如 <c>audio</c>；GUI 页签显隐另行映射）</param>
public sealed record FeatureDescriptor(
    string Id,
    string Name,
    string Description,
    bool DefaultEnabled,
    IReadOnlyList<string> CliDomains);
