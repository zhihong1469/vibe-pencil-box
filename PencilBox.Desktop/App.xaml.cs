using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using PencilBox.Desktop.Helpers;
using PencilBox.Desktop.ViewModels;
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

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<DevicePageViewModel>();
        services.AddSingleton<MacroEditorViewModel>();
        services.AddSingleton<AudioRecorderViewModel>();
        services.AddSingleton<SerialTerminalViewModel>();
        services.AddSingleton<DspPageViewModel>();

        services.AddTransient<MainWindow>();
    }
}
