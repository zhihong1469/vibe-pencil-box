using PencilBox.Cli.Output;
using PencilBox.Core.Configuration;

namespace PencilBox.Cli.Commands;

public class ConfigCommands
{
    private readonly IConfigStore _configStore;

    public ConfigCommands(IConfigStore configStore)
    {
        _configStore = configStore;
    }

    public async Task<int> GetAsync(string? key)
    {
        var settings = await _configStore.LoadSettingsAsync();

        if (string.IsNullOrEmpty(key))
        {
            ConsoleFormatter.PrintObject(settings);
        }
        else
        {
            var prop = settings.GetType().GetProperty(key);
            if (prop == null)
            {
                ConsoleFormatter.Error($"Unknown config key: {key}");
                return 1;
            }
            Console.WriteLine($"{key} = {prop.GetValue(settings)}");
        }
        return 0;
    }

    public async Task<int> SetAsync(string key, string value)
    {
        var settings = await _configStore.LoadSettingsAsync();
        var prop = settings.GetType().GetProperty(key);
        if (prop == null)
        {
            ConsoleFormatter.Error($"Unknown config key: {key}");
            return 1;
        }

        object? converted;
        var propType = prop.PropertyType;

        if (propType == typeof(string))
            converted = value;
        else if (propType == typeof(int))
            converted = int.Parse(value);
        else if (propType == typeof(bool))
            converted = bool.Parse(value);
        else if (propType.IsEnum)
            converted = Enum.Parse(propType, value, ignoreCase: true);
        else
            converted = value;

        prop.SetValue(settings, converted);
        await _configStore.SaveSettingsAsync(settings);
        ConsoleFormatter.Success($"{key} = {value}");
        return 0;
    }

    public async Task<int> PathAsync()
    {
        var settings = await _configStore.LoadSettingsAsync();
        ConsoleFormatter.Info($"Config path: {settings.ConfigFilePath}");
        ConsoleFormatter.Info($"Log path: {settings.LogFilePath}");
        return 0;
    }
}
