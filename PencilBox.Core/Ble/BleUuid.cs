// <copyright file="BleUuid.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Ble;

/// <summary>
/// BLE UUID 解析/规范化（纯逻辑，便于单测）。
/// 接受 SIG 16-bit 短形（`180F`、`0x2A19`）与 128-bit（标准连字符形 / 无连字符 32 HEX），
/// 统一规范化为 GATT 实际返回的**大写标准形**，供 `ble read/write/notify --uuid` 匹配。
/// 背景：服务/特征 UUID 匹配踩过字节序坑（dongle B9），PC 侧匹配一律走字符串规范化比对，不做字节重组。
/// </summary>
public static class BleUuid
{
    /// <summary>SIG 标准 128-bit 基 UUID 尾段</summary>
    public const string SigBase = "0000-1000-8000-00805F9B34FB";

    /// <summary>尝试规范化；非法输入返回 false（不抛异常，CLI 侧给出友好报错）。</summary>
    public static bool TryNormalize(string? text, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var hex = text.Trim()
                      .Replace("0x", "", StringComparison.OrdinalIgnoreCase)
                      .Replace("-", "")
                      .ToUpperInvariant();

        // 16-bit SIG 短形（2A19 / 180F）→ 展开为 128-bit 标准形
        if (hex.Length == 4 && hex.All(Uri.IsHexDigit))
        {
            normalized = $"0000{hex}-{SigBase}";
            return true;
        }

        // 128-bit（32 HEX，含/不含连字符均可）
        if (hex.Length == 32 && hex.All(Uri.IsHexDigit))
        {
            normalized = $"{hex[0..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}";
            return true;
        }

        return false;
    }

    /// <summary>规范化（非法输入抛 <see cref="ArgumentException"/>，供内部确信路径使用）。</summary>
    public static string Normalize(string text) =>
        TryNormalize(text, out var n) ? n : throw new ArgumentException($"非法 BLE UUID：{text}");

    /// <summary>取展示文本：16-bit SIG 族折叠回短形（0000180F-… → 0x180F），其余原样标准形。</summary>
    public static string ToDisplay(string normalizedUuid)
    {
        if (normalizedUuid.Length == 36 &&
            normalizedUuid.StartsWith("0000", StringComparison.Ordinal) &&
            normalizedUuid.EndsWith(SigBase, StringComparison.OrdinalIgnoreCase))
        {
            return $"0x{normalizedUuid[4..8]}";
        }
        return normalizedUuid;
    }
}
