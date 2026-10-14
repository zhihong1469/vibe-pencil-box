// <copyright file="AirMouseSnapshotStore.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PencilBox.Core.Protocol.Company;
using PencilBox.Core.Services;

namespace PencilBox.Infrastructure.Config;

/// <summary>
/// <see cref="IAirMouseSnapshotStore"/> 文件实现（照 <see cref="KeymapConfigStore"/> 模式）：
/// `<exe 同级>/configs/airmouse/&lt;快照名&gt;.json` 一快照一文件；快照名即文件名（非法字符替换 `_`）。
/// 与 keymap 不同：无 `.current`、无出厂再生——快照全是用户产物，删了就是删了（笔端不受影响）。
/// </summary>
public sealed class AirMouseSnapshotStore : IAirMouseSnapshotStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _dir;
    private readonly ILogger<AirMouseSnapshotStore> _logger;

    public AirMouseSnapshotStore(ILogger<AirMouseSnapshotStore> logger, string? baseDirectory = null)
    {
        _dir = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "configs", "airmouse");
        _logger = logger;
    }

    public IReadOnlyList<string> ListNames()
    {
        if (!Directory.Exists(_dir))
            return Array.Empty<string>();

        return Directory.GetFiles(_dir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    public void Save(AirMouseSnapshot snapshot)
    {
        Directory.CreateDirectory(_dir);
        var path = PathOf(snapshot.Name);
        var doc = new SnapshotDocument
        {
            Name = snapshot.Name,
            SavedAt = snapshot.SavedAt,
            Note = snapshot.Note,
            Firmware = snapshot.Firmware,
            Values = snapshot.Values.Select(v => new ValueEntry(v.Id, v.Value)).ToList(),
            Checklist = snapshot.Checklist,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(doc, JsonOpts));
        _logger.LogInformation("空鼠快照已存：{Name} → {Path}（{Count} 项）", snapshot.Name, path, snapshot.Values.Count);
    }

    public AirMouseSnapshot? Load(string name)
    {
        var path = PathOf(name);
        if (!File.Exists(path))
            return null;

        try
        {
            var doc = JsonSerializer.Deserialize<SnapshotDocument>(File.ReadAllText(path));
            if (doc?.Values == null)
                return null;

            return new AirMouseSnapshot(
                string.IsNullOrWhiteSpace(doc.Name) ? name : doc.Name!,
                doc.SavedAt, doc.Note, doc.Firmware,
                doc.Values.Select(v => new TuningParamReading(v.Id, v.Value)).ToList(),
                doc.Checklist);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "空鼠快照读取失败（损坏按不存在处理）：{Path}", path);
            return null;
        }
    }

    public bool Delete(string name)
    {
        var path = PathOf(name);
        if (!File.Exists(path))
            return true;
        File.Delete(path);
        _logger.LogInformation("空鼠快照已删：{Name}", name);
        return true;
    }

    private string PathOf(string name)
    {
        var safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(safe))
            throw new ArgumentException($"快照名非法：\"{name}\"", nameof(name));
        return Path.Combine(_dir, safe + ".json");
    }

    private sealed class SnapshotDocument
    {
        public string? Name { get; set; }
        public DateTime SavedAt { get; set; }
        public string? Note { get; set; }
        public string? Firmware { get; set; }
        public List<ValueEntry>? Values { get; set; }
        public IReadOnlyList<bool>? Checklist { get; set; }
    }

    private sealed record ValueEntry(byte Id, float Value);
}
