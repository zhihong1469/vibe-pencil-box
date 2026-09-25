// <copyright file="JsonConfigStore.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PencilBox.Core.Configuration;
using PencilBox.Core.Models;

namespace PencilBox.Infrastructure.Config;

/// <summary>
/// JSON 配置存储实现
/// </summary>
public class JsonConfigStore : IConfigStore
{
    private readonly ILogger<JsonConfigStore> _logger;
    private readonly string _basePath;
    private readonly JsonSerializerOptions _jsonOptions;

    public JsonConfigStore(ILogger<JsonConfigStore> logger, string? basePath = null)
    {
        _logger = logger;
        _basePath = basePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PencilBox");

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };

        Directory.CreateDirectory(_basePath);
        Directory.CreateDirectory(Path.Combine(_basePath, "config"));
        Directory.CreateDirectory(Path.Combine(_basePath, "logs"));
    }

    public async Task<IReadOnlyList<MacroConfig>> LoadMacrosAsync(
        CancellationToken cancellationToken = default)
    {
        var filePath = GetMacrosFilePath();

        if (!File.Exists(filePath))
        {
            _logger.LogInformation("No macros file found at {Path}, returning empty list", filePath);
            return new List<MacroConfig>();
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath, cancellationToken);
            var macros = JsonSerializer.Deserialize<List<MacroConfig>>(json, _jsonOptions);
            return macros ?? new List<MacroConfig>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load macros from {Path}", filePath);
            return new List<MacroConfig>();
        }
    }

    public async Task SaveMacrosAsync(IEnumerable<MacroConfig> macros,
        CancellationToken cancellationToken = default)
    {
        var filePath = GetMacrosFilePath();

        try
        {
            var json = JsonSerializer.Serialize(macros.ToList(), _jsonOptions);
            await File.WriteAllTextAsync(filePath, json, cancellationToken);
            _logger.LogInformation("Saved macros to {Path}", filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save macros to {Path}", filePath);
            throw;
        }
    }

    public async Task<AppSettings> LoadSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        var filePath = GetSettingsFilePath();

        if (!File.Exists(filePath))
        {
            _logger.LogInformation("No settings file found at {Path}, returning defaults", filePath);
            return new AppSettings
            {
                ConfigFilePath = filePath,
                LogFilePath = Path.Combine(_basePath, "logs", "pencilbox.log")
            };
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath, cancellationToken);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions);

            if (settings != null)
            {
                settings.ConfigFilePath = filePath;
                if (string.IsNullOrEmpty(settings.LogFilePath))
                {
                    settings.LogFilePath = Path.Combine(_basePath, "logs", "pencilbox.log");
                }
                return settings;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load settings from {Path}", filePath);
        }

        return new AppSettings
        {
            ConfigFilePath = filePath,
            LogFilePath = Path.Combine(_basePath, "logs", "pencilbox.log")
        };
    }

    public async Task SaveSettingsAsync(AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        var filePath = GetSettingsFilePath();

        try
        {
            var json = JsonSerializer.Serialize(settings, _jsonOptions);
            await File.WriteAllTextAsync(filePath, json, cancellationToken);
            _logger.LogInformation("Saved settings to {Path}", filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings to {Path}", filePath);
            throw;
        }
    }

    private string GetMacrosFilePath() => Path.Combine(_basePath, "config", "macros.json");
    private string GetSettingsFilePath() => Path.Combine(_basePath, "config", "settings.json");
    private string GetKeymapFilePath() => Path.Combine(_basePath, "config", "keymap.json");

    public async Task<IReadOnlyList<KeymapEntry>> LoadKeymapAsync(
        CancellationToken cancellationToken = default)
    {
        var filePath = GetKeymapFilePath();

        if (!File.Exists(filePath))
        {
            _logger.LogInformation("No keymap cache found at {Path}", filePath);
            return new List<KeymapEntry>();
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath, cancellationToken);
            var entries = JsonSerializer.Deserialize<List<KeymapEntry>>(json, _jsonOptions);
            return entries ?? new List<KeymapEntry>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load keymap cache from {Path}", filePath);
            return new List<KeymapEntry>();
        }
    }

    public async Task SaveKeymapAsync(IEnumerable<KeymapEntry> entries,
        CancellationToken cancellationToken = default)
    {
        var filePath = GetKeymapFilePath();

        try
        {
            var json = JsonSerializer.Serialize(entries.ToList(), _jsonOptions);
            await File.WriteAllTextAsync(filePath, json, cancellationToken);
            _logger.LogInformation("Saved keymap cache to {Path}", filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save keymap cache to {Path}", filePath);
            throw;
        }
    }
}
