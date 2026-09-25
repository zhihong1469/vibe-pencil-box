// <copyright file="KeymapProtocol.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Models;

namespace PencilBox.Core.Protocol;

/// <summary>
/// Keymap 快捷键管理协议（CUSTOM Sub-CMD 0x10~0x17）
/// 对齐 link/host/usb/04_快捷键管理.md
///
/// 所有命令走本地（PC ↔ Dongle），不穿透笔端。
/// </summary>
public static class KeymapProtocol
{
    /// <summary>GET_KEYMAP_INFO（0x15）：读取元信息</summary>
    public static byte[] BuildGetKeymapInfo()
        => PencilBoxProtocol.BuildCustomCommand((byte)PcSubCommand.GetKeymapInfo);

    /// <summary>GET_KEYMAP（0x10）：分页读取条目</summary>
    public static byte[] BuildGetKeymap(byte startIndex, byte maxCount)
        => PencilBoxProtocol.BuildCustomCommand((byte)PcSubCommand.GetKeymap, new[] { startIndex, maxCount });

    /// <summary>SET_KEY_ENTRY（0x11）：写单条（先写 L2 缓存）</summary>
    public static byte[] BuildSetKeyEntry(byte index, KeymapEntry entry)
    {
        var entryBytes = entry.ToBytes();
        var data = new byte[1 + entryBytes.Length];
        data[0] = index;
        entryBytes.CopyTo(data, 1);
        return PencilBoxProtocol.BuildCustomCommand((byte)PcSubCommand.SetKeyEntry, data);
    }

    /// <summary>DELETE_KEY_ENTRY（0x12）：删除单条（恢复默认）</summary>
    public static byte[] BuildDeleteKeyEntry(byte index)
        => PencilBoxProtocol.BuildCustomCommand((byte)PcSubCommand.DeleteKeyEntry, new[] { index });

    /// <summary>RESET_KEYMAP（0x13）：全量恢复出厂默认</summary>
    public static byte[] BuildResetKeymap()
        => PencilBoxProtocol.BuildCustomCommand((byte)PcSubCommand.ResetKeymap);

    /// <summary>COMMIT（0x16）：L2 缓存 → 运行表 + NVDS</summary>
    public static byte[] BuildCommitKeymap()
        => PencilBoxProtocol.BuildCustomCommand((byte)PcSubCommand.CommitKeymap);

    /// <summary>ROLLBACK（0x17）：丢弃 L2 修改</summary>
    public static byte[] BuildRollbackKeymap()
        => PencilBoxProtocol.BuildCustomCommand((byte)PcSubCommand.RollbackKeymap);

    /// <summary>
    /// 解析 GET_KEYMAP（0x10）应答：载荷 = [total][start_idx][count][entries(8B×count)]
    /// 注意：入参 payload 为去掉 Sub-CMD 后的数据（payload[0] 即 total）。
    /// </summary>
    public static bool TryParseGetKeymapResponse(ReadOnlySpan<byte> payload,
        out byte total, out byte startIndex, out List<KeymapEntry> entries)
    {
        total = 0; startIndex = 0; entries = new List<KeymapEntry>();

        if (payload.Length < 3)
            return false;

        total = payload[0];
        startIndex = payload[1];
        byte count = payload[2];

        for (int i = 0; i < count; i++)
        {
            int offset = 3 + i * 8;
            if (offset + 8 > payload.Length)
                break;
            entries.Add(KeymapEntry.FromBytes(payload.Slice(offset, 8)));
        }

        return true;
    }
}
