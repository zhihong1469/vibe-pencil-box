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

        var mainWindow = Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
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
        services.AddDebugToolsModule();
        services.AddShellModule();
    }
}
