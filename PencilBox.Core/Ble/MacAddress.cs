// <copyright file="MacAddress.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Ble;

/// <summary>
/// 蓝牙地址（MAC）字节序工具。
///
/// 两种序在本工程内的约定（与链路/文档对齐，避免散落歧义）：
/// - **显示序（Display order）**：`AA:BB:CC:DD:EE:FF`，数组 = `{AA,BB,CC,DD,EE,FF}`（最高字节在前）。
///   广播/UI/文档/点名均用此序。
/// - **链路序（Link order）**：显示序反转 = `{FF,EE,DD,CC,BB,AA}`。
///   Dongle 命令通道（EP4 A4 SET_TARGET / 裸 6B 注入）要求**链路序**，见 box 测试需求 #7 与坑点速查"MAC 字节序"。
///
/// 仅在此处做一处转换，依赖方（CLI/命令层/字节解析）一律经它，避免双端对拍出错。
/// </summary>
public static class MacAddress
{
    /// <summary>链路序=显示序反转</summary>
    public static byte[] ToLinkOrder(ReadOnlySpan<byte> displayOrder)
    {
        if (displayOrder.Length != 6)
            throw new ArgumentException($"MAC must be 6 bytes, got {displayOrder.Length}", nameof(displayOrder));

        var link = new byte[6];
        for (int i = 0; i < 6; i++)
            link[i] = displayOrder[5 - i];
        return link;
    }

    /// <summary>显示序=链路序反转</summary>
    public static byte[] ToDisplayOrder(ReadOnlySpan<byte> linkOrder)
    {
        if (linkOrder.Length != 6)
            throw new ArgumentException($"MAC must be 6 bytes, got {linkOrder.Length}", nameof(linkOrder));

        var display = new byte[6];
        for (int i = 0; i < 6; i++)
            display[i] = linkOrder[5 - i];
        return display;
    }

    /// <summary>解析显示序字符串（`AA:BB:CC:DD:EE:FF` 或 `AABBCCDDEEFF`）→ 显示序字节数组</summary>
    public static byte[] ParseDisplay(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("MAC display string is empty", nameof(text));

        var cleaned = text.Replace(":", "").Replace("-", "").Replace(" ", "");
        if (cleaned.Length != 12)
            throw new ArgumentException($"MAC display string must be 12 hex chars, got '{text}'", nameof(text));

        var bytes = new byte[6];
        for (int i = 0; i < 6; i++)
            bytes[i] = Convert.ToByte(cleaned.Substring(i * 2, 2), 16);
        return bytes;
    }

    /// <summary>显示序字节数组 → `AA:BB:CC:DD:EE:FF` 文本</summary>
    public static string FormatDisplay(ReadOnlySpan<byte> displayOrder)
    {
        if (displayOrder.Length != 6)
            return string.Empty;
        return string.Join(":", displayOrder.ToArray().Select(b => b.ToString("X2")));
    }
}
