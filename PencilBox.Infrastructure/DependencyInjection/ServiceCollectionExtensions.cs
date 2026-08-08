using Microsoft.Extensions.DependencyInjection;
using PencilBox.Core.Communication;
using PencilBox.Core.Configuration;
using PencilBox.Core.Services;
using PencilBox.Infrastructure.Audio;
using PencilBox.Infrastructure.Config;
using PencilBox.Infrastructure.Dsp;
using PencilBox.Infrastructure.Hid;
using PencilBox.Infrastructure.Serial;
using PencilBox.Infrastructure.Services;

namespace PencilBox.Infrastructure.DependencyInjection;

/// <summary>
/// Infrastructure layer options for DI configuration
/// </summary>
public class InfrastructureOptions
{
    /// <summary>
    /// Python executable path for DSP (empty = auto-detect)
    /// </summary>
    public string PythonPath { get; set; } = string.Empty;

    /// <summary>
    /// DSP scripts directory path
    /// </summary>
    public string DspScriptsPath { get; set; } = string.Empty;

    /// <summary>
    /// DSP execution timeout in milliseconds
    /// </summary>
    public int DspTimeoutMs { get; set; } = 30000;
}

/// <summary>
/// Shared DI registration for PencilBox infrastructure services.
/// Used by CLI, MCP, and Desktop alike.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register all PencilBox infrastructure services
    /// </summary>
    public static IServiceCollection AddPencilBoxInfrastructure(
        this IServiceCollection services,
        Action<InfrastructureOptions>? configure = null)
    {
        var options = new InfrastructureOptions();
        configure?.Invoke(options);

        // Transport layer
        services.AddSingleton<IHidTransport, HidSharpTransport>();
        services.AddSingleton<ISerialTransport, SerialTransport>();

        // Audio capture
        services.AddSingleton<IAudioCapture, NAudioAudioCapture>();

        // Business services
        services.AddSingleton<IDeviceService, DeviceService>();
        services.AddSingleton<IMacroService, MacroService>();
        services.AddSingleton<IAudioService, AudioService>();

        // Configuration storage
        services.AddSingleton<IConfigStore, JsonConfigStore>();

        // DSP service
        services.AddSingleton<IDspService>(sp =>
        {
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PythonProcessBridge>>();
            return new PythonProcessBridge(
                logger,
                string.IsNullOrEmpty(options.PythonPath) ? null : options.PythonPath,
                string.IsNullOrEmpty(options.DspScriptsPath) ? null : options.DspScriptsPath,
                options.DspTimeoutMs);
        });

        return services;
    }
}
