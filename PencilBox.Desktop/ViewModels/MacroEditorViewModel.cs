using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using PencilBox.Core.Models;
using PencilBox.Core.Services;
using PencilBox.Desktop.Helpers;

namespace PencilBox.Desktop.ViewModels;

public partial class MacroEditorViewModel : ObservableObject
{
    private readonly IMacroService _macroService;

    [ObservableProperty]
    private ObservableCollection<MacroConfig> _macros = new();

    [ObservableProperty]
    private MacroConfig? _selectedMacro;

    [ObservableProperty]
    private string _newMacroName = string.Empty;

    [ObservableProperty]
    private string _newMacroDescription = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private bool _isExecuting;

    [ObservableProperty]
    private bool _isRecording;

    public MacroEditorViewModel(IMacroService macroService)
    {
        _macroService = macroService;
        _macroService.MacroListChanged += OnMacroListChanged;
        _macroService.ExecutionStateChanged += OnExecutionStateChanged;
    }

    private void OnMacroListChanged(object? sender, IReadOnlyList<MacroConfig> macros)
    {
        MainThreadHelper.EnsureOnMainThread(() =>
        {
            Macros = new ObservableCollection<MacroConfig>(macros);
        });
    }

    private void OnExecutionStateChanged(object? sender, MacroExecutionState state)
    {
        MainThreadHelper.EnsureOnMainThread(() =>
        {
            IsExecuting = state == MacroExecutionState.Running;
            IsRecording = state == MacroExecutionState.Recording;
            StatusMessage = state switch
            {
                MacroExecutionState.Running => "Executing macro...",
                MacroExecutionState.Recording => "Recording macro...",
                MacroExecutionState.Paused => "Paused",
                _ => "Ready"
            };
        });
    }

    [RelayCommand]
    private void LoadMacros()
    {
        try
        {
            StatusMessage = "Loading macros...";
            var macros = _macroService.GetAllMacros();
            Macros = new ObservableCollection<MacroConfig>(macros);
            StatusMessage = "Macros loaded";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task AddMacroAsync()
    {
        if (string.IsNullOrWhiteSpace(NewMacroName))
        {
            StatusMessage = "Please enter a macro name";
            return;
        }

        try
        {
            var macro = new MacroConfig
            {
                Name = NewMacroName,
                Description = string.IsNullOrWhiteSpace(NewMacroDescription) ? "New macro" : NewMacroDescription,
                IsEnabled = true,
                KeySequence = new List<KeyAction>()
            };

            await _macroService.AddMacroAsync(macro);
            NewMacroName = string.Empty;
            StatusMessage = $"Added macro: {macro.Name}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DeleteMacroAsync()
    {
        if (SelectedMacro == null) return;

        try
        {
            await _macroService.DeleteMacroAsync(SelectedMacro.Id);
            StatusMessage = $"Deleted macro: {SelectedMacro.Name}";
            SelectedMacro = null;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExecuteMacroAsync()
    {
        if (SelectedMacro == null) return;

        try
        {
            StatusMessage = $"Executing: {SelectedMacro.Name}";
            await _macroService.ExecuteMacroAsync(SelectedMacro.Id);
            StatusMessage = "Execution completed";
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = $"Cannot execute: {ex.Message}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task StopExecutionAsync()
    {
        try
        {
            await _macroService.StopExecutionAsync();
            StatusMessage = "Execution stopped";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RecordMacroAsync()
    {
        if (string.IsNullOrWhiteSpace(NewMacroName))
        {
            StatusMessage = "Please enter a macro name";
            return;
        }

        try
        {
            var macro = await _macroService.RecordMacroAsync(NewMacroName);
            NewMacroName = string.Empty;
            StatusMessage = $"Recorded macro: {macro.Name}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }
}
