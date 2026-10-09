// <copyright file="KeymapConfigStore.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PencilBox.Core.Models;
using PencilBox.Core.Services;

namespace PencilBox.Infrastructure.Config;

/// <summary>
/// <see cref="IKeymapConfigStore"/> 文件实现（pc-issues/04 提案 B）：
/// `<exe 同级>/configs/keymap/&lt;配置名&gt;.json` 一配置一文件；`.current` 记当前选中；
/// 出厂默认文件内容恒由 <see cref="KeymapFactoryDefault"/> 再生（用户手工改动会被 Initialize 覆写回）。
/// </summary>
public sealed class KeymapConfigStore : IKeymapConfigStore
{
    private const string CurrentFileName = ".current";
    private const string MigratedName = "本地缓存";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _dir;
    private readonly string _currentPath;
    private readonly string? _legacyCachePath;
    private readonly ILogger<KeymapConfigStore> _logger;

    public string FactoryName => "出厂默认";

    public KeymapConfigStore(ILogger<KeymapConfigStore> logger,
        string? baseDirectory = null, string? legacyCachePath = null)
    {
        _dir = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "configs", "keymap");
        _currentPath = Path.Combine(_dir, CurrentFileName);
        _legacyCachePath = legacyCachePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PencilBox", "config", "keymap.json");
        _logger = logger;
    }

    public void Initialize()
    {
        Directory.CreateDirectory(_dir);

        // 出厂默认文件：每次启动按常量表再生（单一来源=KeymapFactoryDefault，用户手改会被覆盖回）
        WriteDocument(new KeymapConfigDocument
        {
            Name = FactoryName,
            CreatedAt = DateTime.Now,
            Entries = KeymapSorting.Canonical(KeymapFactoryDefault.Entries),
        });

        MigrateLegacyCache();
    }

    public IReadOnlyList<string> ListNames()
    {
        if (!Directory.Exists(_dir))
            return [FactoryName];

        var names = Directory.GetFiles(_dir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        names.RemoveAll(n => string.Equals(n, FactoryName, StringComparison.Ordinal));
        names.Insert(0, FactoryName);
        return names;
    }

    public KeymapConfigDocument? Load(string name)
    {
        var path = PathOf(name);
        if (!File.Exists(path))
            return null;

        try
        {
            var doc = JsonSerializer.Deserialize<KeymapConfigDocument>(File.ReadAllText(path), JsonOpts);
            if (doc != null)
            {
                doc.Name = name;
                doc.Entries = KeymapSorting.Canonical(doc.Entries);   // 归一化：旧文件/迁移文件也按规范序
            }
            return doc;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Keymap 配置损坏或不可读：{Path}", path);
            return null;
        }
    }

    public void Save(string name, IEnumerable<KeymapEntry> entries)
    {
        EnsureUserConfig(name);
        WriteDocument(new KeymapConfigDocument
        {
            Name = name,
            CreatedAt = DateTime.Now,
            Entries = KeymapSorting.Canonical(entries),
        });
    }

    public void Rename(string oldName, string newName)
    {
        EnsureUserConfig(oldName);
        EnsureUserConfig(newName);
        if (File.Exists(PathOf(newName)))
            throw new IOException($"配置「{newName}」已存在");

        // 是否当前配置必须在移动前判：GetCurrentName 有存在性校验，移动后旧名即"失效"
        var wasCurrent = string.Equals(GetCurrentName(), oldName, StringComparison.Ordinal);
        File.Move(PathOf(oldName), PathOf(newName));
        if (wasCurrent)
            SetCurrentName(newName);
    }

    public void Delete(string name)
    {
        EnsureUserConfig(name);
        var wasCurrent = string.Equals(GetCurrentName(), name, StringComparison.Ordinal);
        File.Delete(PathOf(name));
        if (wasCurrent)
            SetCurrentName(FactoryName);
    }

    public string GetCurrentName()
    {
        try
        {
            var name = File.Exists(_currentPath) ? File.ReadAllText(_currentPath).Trim() : string.Empty;
            return File.Exists(PathOf(name)) ? name : FactoryName;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取 .current 失败，回落出厂默认");
            return FactoryName;
        }
    }

    public void SetCurrentName(string name)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_currentPath, name);
    }

    /// <summary>旧单缓存迁移：`%LocalAppData%/PencilBox/config/keymap.json` → "本地缓存"（幂等，原文件保留）</summary>
    private void MigrateLegacyCache()
    {
        try
        {
            if (_legacyCachePath == null || !File.Exists(_legacyCachePath) || File.Exists(PathOf(MigratedName)))
                return;

            var entries = JsonSerializer.Deserialize<List<KeymapEntry>>(
                File.ReadAllText(_legacyCachePath), JsonOpts);
            if (entries is not { Count: > 0 })
                return;

            WriteDocument(new KeymapConfigDocument { Name = MigratedName, Entries = entries });
            _logger.LogInformation("旧 Keymap 缓存已迁移为配置「{Name}」（{Count} 条，原文件保留）",
                MigratedName, entries.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "旧 Keymap 缓存迁移失败（不阻塞启动）：{Path}", _legacyCachePath);
        }
    }

    private void WriteDocument(KeymapConfigDocument doc)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathOf(doc.Name), JsonSerializer.Serialize(doc, JsonOpts));
    }

    private string PathOf(string name) => Path.Combine(_dir, $"{name}.json");

    private void EnsureUserConfig(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("配置名不能为空");
        if (name.Contains('.') && name != MigratedName || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"配置名含非法字符：{name}");
        if (string.Equals(name, FactoryName, StringComparison.Ordinal))
            throw new InvalidOperationException("出厂默认配置只读，不可修改/删除");
    }
}
