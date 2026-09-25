// <copyright file="JsonPenCatalogStore.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PencilBox.Core.Ble;
using PencilBox.Core.Configuration;

namespace PencilBox.Infrastructure.Config;

/// <summary>
/// 已知笔端目录的 JSON 文件持久化（%LocalAppData%/PencilBox/config/pens.json）。
/// 与 JsonConfigStore 同范式；只存 KnownPen（MAC 显示序为单源）。
/// </summary>
public class JsonPenCatalogStore : IPenCatalogStore
{
    private readonly ILogger<JsonPenCatalogStore> _logger;
    private readonly string _filePath;
    private readonly JsonSerializerOptions _jsonOptions;

    public JsonPenCatalogStore(ILogger<JsonPenCatalogStore> logger, string? basePath = null)
    {
        _logger = logger;
        _filePath = Path.Combine(
            basePath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PencilBox"),
            "config", "pens.json");

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };

        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
    }

    public async Task<IReadOnlyList<KnownPen>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            _logger.LogInformation("No pens catalog found at {Path}, returning empty", _filePath);
            return new List<KnownPen>();
        }

        try
        {
            var json = await File.ReadAllTextAsync(_filePath, cancellationToken);
            var pens = JsonSerializer.Deserialize<List<KnownPen>>(json, _jsonOptions);
            return pens ?? new List<KnownPen>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load pens catalog from {Path}", _filePath);
            return new List<KnownPen>();
        }
    }

    public async Task SaveAllAsync(IEnumerable<KnownPen> pens, CancellationToken cancellationToken = default)
    {
        try
        {
            var json = JsonSerializer.Serialize(pens.ToList(), _jsonOptions);
            await File.WriteAllTextAsync(_filePath, json, cancellationToken);
            _logger.LogInformation("Saved pens catalog ({Count}) to {Path}", pens.Count(), _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save pens catalog to {Path}", _filePath);
            throw;
        }
    }

    public Task ClearAllAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(_filePath))
        {
            File.Delete(_filePath);
            _logger.LogInformation("Cleared pens catalog ({Path})", _filePath);
        }
        return Task.CompletedTask;
    }
}
