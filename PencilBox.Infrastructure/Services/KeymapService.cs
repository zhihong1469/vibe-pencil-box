using Microsoft.Extensions.Logging;
using PencilBox.Core.Configuration;
using PencilBox.Core.Models;
using PencilBox.Core.Protocol;
using PencilBox.Core.Services;

namespace PencilBox.Infrastructure.Services;

/// <summary>
/// Keymap 快捷键管理服务实现
/// 从 Dongle 实时读取 + 修改 + 提交/回滚，并维护本地 JSON 缓存（掉电不丢失）。
/// 对齐 link/host/usb/04_快捷键管理.md
/// </summary>
public class KeymapService : IKeymapService
{
    private readonly ICommandClient _commandClient;
    private readonly IConfigStore _configStore;
    private readonly ILogger<KeymapService> _logger;

    private List<KeymapEntry> _cachedKeymap = new();

    public IReadOnlyList<KeymapEntry> CachedKeymap => _cachedKeymap;

    public event EventHandler<IReadOnlyList<KeymapEntry>>? KeymapChanged;

    public KeymapService(ICommandClient commandClient, IConfigStore configStore, ILogger<KeymapService> logger)
    {
        _commandClient = commandClient;
        _configStore = configStore;
        _logger = logger;
    }

    public async Task<KeymapInfo?> GetKeymapInfoAsync(CancellationToken ct = default)
    {
        var payload = await _commandClient.SendCustomAsync(
            PcSubCommand.GetKeymapInfo, null, 2000, ct);

        if (payload == null || payload.Length < 5)
            return null;

        var info = KeymapInfo.FromBytes(payload);
        _logger.LogInformation("Keymap info: Ver={Ver}, Count={Count}, Mode={Mode}, Max={Max}",
            info.Version, info.EntryCount, info.Mode, info.MaxEntries);
        return info;
    }

    public async Task<IReadOnlyList<KeymapEntry>> ReadKeymapAsync(CancellationToken ct = default)
    {
        if (!_commandClient.IsConnected)
        {
            _logger.LogWarning("Not connected, returning cached keymap");
            return _cachedKeymap;
        }

        var info = await GetKeymapInfoAsync(ct);
        int total = info?.EntryCount ?? 0;

        var entries = new List<KeymapEntry>();
        int start = 0;

        while (start < total)
        {
            // 每帧最多 (56-4)/8 = 6 条
            byte pageSize = 6;
            var payload = await _commandClient.SendCustomAsync(
                PcSubCommand.GetKeymap, new[] { (byte)start, pageSize }, 3000, ct);

            if (payload == null)
                break;

            if (!KeymapProtocol.TryParseGetKeymapResponse(payload, out _, out _, out var page))
                break;

            entries.AddRange(page);
            start += page.Count;

            if (page.Count == 0)
                break;
        }

        _cachedKeymap = entries;
        KeymapChanged?.Invoke(this, entries);
        _logger.LogInformation("Read {Count} keymap entries from Dongle", entries.Count);
        return entries;
    }

    public async Task<bool> SetKeyEntryAsync(byte index, KeymapEntry entry, CancellationToken ct = default)
    {
        var payload = await _commandClient.SendCustomAsync(
            PcSubCommand.SetKeyEntry, BuildSetKeyEntryData(index, entry), 2000, ct);

        if (payload == null || payload.Length < 1)
            return false;

        bool ok = payload[0] == 0x00;
        _logger.LogInformation("SetKeyEntry idx={Index} => {Result}", index, ok ? "OK" : $"0x{payload[0]:X2}");
        return ok;
    }

    public async Task<bool> DeleteKeyEntryAsync(byte index, CancellationToken ct = default)
    {
        var payload = await _commandClient.SendCustomAsync(
            PcSubCommand.DeleteKeyEntry, new[] { index }, 2000, ct);

        return payload != null && payload.Length >= 1 && payload[0] == 0x00;
    }

    public async Task<bool> ResetKeymapAsync(CancellationToken ct = default)
    {
        var payload = await _commandClient.SendCustomAsync(
            PcSubCommand.ResetKeymap, null, 3000, ct);

        bool ok = payload != null && payload.Length >= 1 && payload[0] == 0x00;
        if (ok)
        {
            _cachedKeymap.Clear();
            KeymapChanged?.Invoke(this, _cachedKeymap);
        }
        return ok;
    }

    public async Task<bool> CommitAsync(CancellationToken ct = default)
    {
        var payload = await _commandClient.SendCustomAsync(
            PcSubCommand.CommitKeymap, null, 3000, ct);

        bool ok = payload != null && payload.Length >= 1 && payload[0] == 0x00;
        if (ok)
            await SaveCacheAsync(_cachedKeymap, ct);
        return ok;
    }

    public async Task<bool> RollbackAsync(CancellationToken ct = default)
    {
        var payload = await _commandClient.SendCustomAsync(
            PcSubCommand.RollbackKeymap, null, 2000, ct);

        return payload != null && payload.Length >= 1 && payload[0] == 0x00;
    }

    public async Task LoadCacheAsync(CancellationToken ct = default)
    {
        try
        {
            var entries = await _configStore.LoadKeymapAsync(ct);
            _cachedKeymap = entries.ToList();
            _logger.LogInformation("Loaded {Count} keymap entries from local cache", _cachedKeymap.Count);
            KeymapChanged?.Invoke(this, _cachedKeymap);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load keymap cache");
        }
    }

    public async Task SaveCacheAsync(IEnumerable<KeymapEntry> entries, CancellationToken ct = default)
    {
        try
        {
            _cachedKeymap = entries.ToList();
            await _configStore.SaveKeymapAsync(_cachedKeymap, ct);
            _logger.LogInformation("Saved {Count} keymap entries to local cache", _cachedKeymap.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save keymap cache");
        }
    }

    private static byte[] BuildSetKeyEntryData(byte index, KeymapEntry entry)
    {
        var entryBytes = entry.ToBytes();
        var data = new byte[1 + entryBytes.Length];
        data[0] = index;
        entryBytes.CopyTo(data, 1);
        return data;
    }
}
