// <copyright file="App.xaml.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using PencilBox.Desktop.Helpers;
using PencilBox.Desktop.ViewModels;
using PencilBox.Infrastructure.Audio;
using PencilBox.Infrastructure.DependencyInjection;

namespace PencilBox.Desktop;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>主窗引用（对话框 Owner 用；启动前为 null）。</summary>
    public static Window? MainWindow { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File("pencilbox.log", rollingInterval: RollingInterval.Day,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        var serviceCollection = new ServiceCollection();
        ConfigureServices(serviceCollection);

        Services = serviceCollection.BuildServiceProvider();

        MainThreadHelper.Initialize(Dispatcher);

        Log.Information("PencilBox Desktop application started");

        // 软触发引擎随宿主常驻（13 号寄宿形态）：启用即监听，无需打开页签；停用态保持停机（VM 负责 re-enable 拉起）
        StartSoftTriggerEngine();

        var mainWindow = Services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    private static void StartSoftTriggerEngine()
    {
        try
        {
            var toggles = Services.GetRequiredService<PencilBox.Core.Features.IFeatureToggleService>();
            if (toggles.IsEnabled("softtrigger"))
                Services.GetRequiredService<PencilBox.Core.SoftTrigger.ISoftTriggerEngine>().Start();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "软触发引擎启动失败（功能其余部分不受影响）");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("PencilBox Desktop application exiting");
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddLogging(builder =>
        {
            builder.AddSerilog(dispose: true);
        });

        // Register all shared infrastructure services (HID, Serial, Audio, DSP, Config, Services)
        services.AddPencilBoxInfrastructure();

        // 延迟快测底座（box/07 §2.1，与 CLI 同源）；引擎主 ctor 吃非泛型 ILogger
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger>(sp => sp.GetRequiredService<ILogger<LatencyProbeEngine>>());
        services.AddSingleton<LatencyProbeEngine>();

        // ===== 交互层按域注册（14 号架构提案：模块化接缝；域划分铁律见 DesktopModuleExtensions） =====
        services.AddFeaturesModule();
        services.AddDashboardModule();
        services.AddOtaModule();
        services.AddSerialScopeModule();
        services.AddPassthroughModule();
        services.AddAirMouseModule();
        services.AddSoftTriggerModule();
        services.AddDebugToolsModule();
        services.AddShellModule();
    }
}
