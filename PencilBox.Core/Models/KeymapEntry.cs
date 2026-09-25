// <copyright file="KeymapEntry.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Protocol;

namespace PencilBox.Core.Models;

/// <summary>
/// Keymap 条目（8 字节，三端共享）
/// 对齐 link/host/usb/04_快捷键管理.md §2
///
/// 字节布局：
/// [0] key_id     物理键 0x01~0x05；组合键 0xF0~0xFF
/// [1] event_type CLICK/DOUBLE/LONG_PRESS/LONG_RELEASE/LONG_SPECIAL
/// [2] action_type NONE/KEYBOARD/MOUSE_BTN/CONSUMER/SPECIAL/APP_NOTIFY
/// [3] modifier   仅 KEYBOARD 有效（LeftShift 等）
/// [4] code_lo    键值低字节
/// [5] code_hi    键值高字节（Consumer 16bit）
/// [6] code_ex    扩展字节
/// [7] reserved   保留
/// </summary>
public class KeymapEntry
{
    public byte KeyId { get; set; }
    public KeyEventType EventType { get; set; }
    public KeyActionType ActionType { get; set; }
    public byte Modifier { get; set; }
    public byte CodeLo { get; set; }
    public byte CodeHi { get; set; }
    public byte CodeEx { get; set; }
    public byte Reserved { get; set; }

    /// <summary>序列化为 8 字节二进制</summary>
    public byte[] ToBytes()
    {
        return new[]
        {
            KeyId,
            (byte)EventType,
            (byte)ActionType,
            Modifier,
            CodeLo,
            CodeHi,
            CodeEx,
            Reserved
        };
    }

    /// <summary>从 8 字节二进制反序列化</summary>
    public static KeymapEntry FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
            throw new ArgumentException($"Keymap entry requires 8 bytes, got {data.Length}");

        return new KeymapEntry
        {
            KeyId = data[0],
            EventType = (KeyEventType)data[1],
            ActionType = (KeyActionType)data[2],
            Modifier = data[3],
            CodeLo = data[4],
            CodeHi = data[5],
            CodeEx = data[6],
            Reserved = data[7]
        };
    }

    /// <summary>克隆</summary>
    public KeymapEntry Clone() => new()
    {
        KeyId = KeyId,
        EventType = EventType,
        ActionType = ActionType,
        Modifier = Modifier,
        CodeLo = CodeLo,
        CodeHi = CodeHi,
        CodeEx = CodeEx,
        Reserved = Reserved
    };
}

/// <summary>
/// Keymap 元信息（GET_KEYMAP_INFO 0x15 应答）
/// [ver_h][ver_l][entry_count][mode][max_entries]
/// </summary>
public class KeymapInfo
{
    public ushort Version { get; set; }
    public byte EntryCount { get; set; }
    public KeymapMode Mode { get; set; }
    public byte MaxEntries { get; set; }

    public static KeymapInfo FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5)
            return new KeymapInfo();

        return new KeymapInfo
        {
            Version = (ushort)((data[0] << 8) | data[1]),
            EntryCount = data[2],
            Mode = (KeymapMode)data[3],
            MaxEntries = data[4]
        };
    }
}

/// <summary>Keymap 模式</summary>
public enum KeymapMode : byte
{
    FactoryDefault = 0x00,
    Custom = 0x01,
}
