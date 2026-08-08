using PencilBox.Cli.Output;
using PencilBox.Core.Models;
using PencilBox.Core.Services;

namespace PencilBox.Cli.Commands;

public class MacroCommands
{
    private readonly IMacroService _macroService;

    public MacroCommands(IMacroService macroService)
    {
        _macroService = macroService;
    }

    public int List()
    {
        var macros = _macroService.GetAllMacros();
        ConsoleFormatter.Info($"Found {macros.Count} macro(s)");
        
        ConsoleFormatter.PrintTable(macros,
            ("ID", m => m.Id[..8]),
            ("Name", m => m.Name),
            ("Keys", m => $"{m.KeySequence.Count}"),
            ("Enabled", m => m.IsEnabled ? "Y" : "N"));
        return 0;
    }

    public async Task<int> RunAsync(string id, bool loop, int count)
    {
        var macro = _macroService.GetMacroById(id);
        if (macro == null)
        {
            ConsoleFormatter.Error($"Macro not found: {id}");
            return 1;
        }

        ConsoleFormatter.Info($"Running macro: {macro.Name}");
        await _macroService.ExecuteMacroAsync(id);
        ConsoleFormatter.Success("Macro completed");
        return 0;
    }

    public async Task<int> AddAsync(string name, string keys, int interval)
    {
        var keyList = keys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseKeyString)
            .ToList();

        var config = new MacroConfig
        {
            Name = name,
            KeySequence = keyList,
            KeyIntervalMs = interval
        };

        await _macroService.AddMacroAsync(config);
        ConsoleFormatter.Success($"Added macro: {name} (ID: {config.Id})");
        return 0;
    }

    public async Task<int> DeleteAsync(string id)
    {
        await _macroService.DeleteMacroAsync(id);
        ConsoleFormatter.Success($"Deleted: {id}");
        return 0;
    }

    private static KeyAction ParseKeyString(string key)
    {
        ushort code = key.ToUpperInvariant() switch
        {
            "A" => 0x04, "B" => 0x05, "C" => 0x06, "D" => 0x07,
            "E" => 0x08, "F" => 0x09, "G" => 0x0A, "H" => 0x0B,
            "ENTER" => 0x28, "SPACE" => 0x2C, "ESC" => 0x29,
            _ => 0x00
        };

        return new KeyAction { Type = KeyType.Keyboard, KeyCode = code, DurationMs = 100 };
    }
}
