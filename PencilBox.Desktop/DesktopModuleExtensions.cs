// <copyright file="DesktopModuleExtensions.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using Microsoft.Extensions.DependencyInjection;
using PencilBox.Desktop.ViewModels;

namespace PencilBox.Desktop;

/// <summary>
/// Desktop 交互层按域拆分的 DI 注册（14 号架构提案 P0：模块化接缝①）。
///
/// 域划分铁律（指挥官 2026-10-19 二轮裁决）：
/// - 核心域（仪表盘/升级）= 设备强相关功能（连接/绑定/笔端状态/Keymap/OTA），不可关、不进功能控制台；
/// - 可选域（扩音/空鼠/串口/调试工具）进 FeatureCatalog（14 号），GUI 页签与 CLI 域受总开关门禁；
/// - 解耦验收：摘掉任一 AddXxxModule() 应用照常启动，其余模块功能完整；
/// - 未来动作库（12/13 号，纯软件域）进场 = 新增 AddMacroModule() + FeatureCatalog 一行，零设备依赖红线。
/// </summary>
public static class DesktopModuleExtensions
{
    /// <summary>核心域·仪表盘：连接/绑定/笔端状态/K2/Keymap 编辑（Keymap 留原位——须连基站 0x55，与连接同域）。</summary>
    public static IServiceCollection AddDashboardModule(this IServiceCollection services) =>
        services.AddSingleton<ProductHomeViewModel>();

    /// <summary>核心域·升级（OTA-first 部署指令的载体，不可关）。</summary>
    public static IServiceCollection AddOtaModule(this IServiceCollection services) =>
        services.AddSingleton<OtaPageViewModel>();

    /// <summary>可选域·串口（FeatureCatalog: seriallog）。</summary>
    public static IServiceCollection AddSerialScopeModule(this IServiceCollection services) =>
        services.AddSingleton<SerialScopeViewModel>();

    /// <summary>可选域·扩音（FeatureCatalog: audio）。</summary>
    public static IServiceCollection AddPassthroughModule(this IServiceCollection services) =>
        services.AddSingleton<PassthroughPageViewModel>();

    /// <summary>可选域·空鼠（FeatureCatalog: airmouse）。</summary>
    public static IServiceCollection AddAirMouseModule(this IServiceCollection services) =>
        services.AddSingleton<AirMouseViewModel>();

    /// <summary>功能控制台（可选功能总开关；壳与仪表盘共用同一单例）。</summary>
    public static IServiceCollection AddFeaturesModule(this IServiceCollection services) =>
        services.AddSingleton<FeaturesViewModel>();

    /// <summary>调试工具（研发版；Release 经 EnableDebugTools 编译开关隐藏入口，VM 注册无害）。</summary>
    public static IServiceCollection AddDebugToolsModule(this IServiceCollection services) =>
        services
            .AddSingleton<DevicePageViewModel>()
            .AddSingleton<MacroEditorViewModel>()
            .AddSingleton<AudioRecorderViewModel>()
            .AddSingleton<DspPageViewModel>();

    /// <summary>壳：主窗聚合各域 VM + 主窗本体。</summary>
    public static IServiceCollection AddShellModule(this IServiceCollection services) =>
        services
            .AddSingleton<MainViewModel>()
            .AddTransient<MainWindow>();
}
