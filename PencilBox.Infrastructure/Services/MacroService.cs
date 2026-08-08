using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PencilBox.Core.Models;
using PencilBox.Core.Services;

namespace PencilBox.Infrastructure.Services;

/// <summary>
/// 宏配置服务实现
/// </summary>
public class MacroService : IMacroService
{
    private readonly ILogger<MacroService> _logger;
    private readonly ConcurrentDictionary<string, MacroConfig> _macros = new();
    private MacroExecutionState _currentState = MacroExecutionState.Idle;
    private CancellationTokenSource? _executionCts;

    public MacroExecutionState CurrentState => _currentState;

    public event EventHandler<IReadOnlyList<MacroConfig>>? MacroListChanged;
    public event EventHandler<MacroExecutionState>? ExecutionStateChanged;

    public MacroService(ILogger<MacroService> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<MacroConfig> GetAllMacros()
    {
        return _macros.Values.ToList().AsReadOnly();
    }

    public MacroConfig? GetMacroById(string id)
    {
        _macros.TryGetValue(id, out var macro);
        return macro;
    }

    public Task AddMacroAsync(MacroConfig config, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(config.Id))
            config.Id = Guid.NewGuid().ToString();

        config.CreatedAt = DateTime.Now;
        config.UpdatedAt = DateTime.Now;

        _macros.TryAdd(config.Id, config);

        MacroListChanged?.Invoke(this, GetAllMacros());
        _logger.LogInformation("Macro added: {Name} ({Id})", config.Name, config.Id);

        return Task.CompletedTask;
    }

    public Task UpdateMacroAsync(MacroConfig config, CancellationToken cancellationToken = default)
    {
        config.UpdatedAt = DateTime.Now;
        _macros.AddOrUpdate(config.Id, config, (_, _) => config);

        MacroListChanged?.Invoke(this, GetAllMacros());
        _logger.LogInformation("Macro updated: {Name} ({Id})", config.Name, config.Id);

        return Task.CompletedTask;
    }

    public Task DeleteMacroAsync(string id, CancellationToken cancellationToken = default)
    {
        _macros.TryRemove(id, out _);
        MacroListChanged?.Invoke(this, GetAllMacros());
        _logger.LogInformation("Macro deleted: {Id}", id);

        return Task.CompletedTask;
    }

    public async Task ExecuteMacroAsync(string macroId, CancellationToken cancellationToken = default)
    {
        if (!_macros.TryGetValue(macroId, out var macro))
            throw new InvalidOperationException($"Macro not found: {macroId}");

        SetState(MacroExecutionState.Running);
        _logger.LogInformation("Executing macro: {Name}", macro.Name);

        _executionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            int stepIndex = 0;
            foreach (var action in macro.KeySequence)
            {
                _executionCts.Token.ThrowIfCancellationRequested();

                await ExecuteActionAsync(action, _executionCts.Token);
                stepIndex++;
            }

            SetState(MacroExecutionState.Idle);
            _logger.LogInformation("Macro completed: {Name}", macro.Name);
        }
        catch (OperationCanceledException)
        {
            SetState(MacroExecutionState.Idle);
            _logger.LogInformation("Macro stopped: {Name}", macro.Name);
        }
        catch (Exception ex)
        {
            SetState(MacroExecutionState.Idle);
            _logger.LogError(ex, "Macro failed: {Name}", macro.Name);
            throw;
        }
    }

    public Task StopExecutionAsync()
    {
        _executionCts?.Cancel();
        SetState(MacroExecutionState.Idle);
        _logger.LogInformation("Stop requested for current macro");
        return Task.CompletedTask;
    }

    public async Task<MacroConfig> RecordMacroAsync(string name, CancellationToken cancellationToken = default)
    {
        SetState(MacroExecutionState.Recording);
        _logger.LogInformation("Recording macro: {Name}", name);

        await Task.Delay(1000, cancellationToken);

        var macro = new MacroConfig
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            Description = "Recorded macro",
            KeySequence = new List<KeyAction>(),
            IsEnabled = true,
            CreatedAt = DateTime.Now
        };

        _macros.TryAdd(macro.Id, macro);
        MacroListChanged?.Invoke(this, GetAllMacros());

        SetState(MacroExecutionState.Idle);
        _logger.LogInformation("Macro recorded: {Name}", name);

        return macro;
    }

    private async Task ExecuteActionAsync(KeyAction action, CancellationToken cancellationToken)
    {
        switch (action.Type)
        {
            case KeyType.Keyboard:
                await ExecuteKeyboardAsync(action, cancellationToken);
                break;

            case KeyType.Consumer:
                await ExecuteConsumerAsync(action, cancellationToken);
                break;

            case KeyType.MouseButton:
            case KeyType.MouseMove:
                await Task.Delay(action.DurationMs, cancellationToken);
                break;

            case KeyType.Delay:
                await Task.Delay(action.DurationMs + action.DelayMs, cancellationToken);
                break;

            default:
                await Task.Delay(action.DurationMs, cancellationToken);
                break;
        }
    }

    private static async Task ExecuteKeyboardAsync(KeyAction action, CancellationToken ct)
    {
        await Task.Delay(action.DurationMs, ct);
    }

    private static async Task ExecuteConsumerAsync(KeyAction action, CancellationToken ct)
    {
        await Task.Delay(action.DurationMs, ct);
    }

    private void SetState(MacroExecutionState state)
    {
        _currentState = state;
        ExecutionStateChanged?.Invoke(this, state);
    }
}
