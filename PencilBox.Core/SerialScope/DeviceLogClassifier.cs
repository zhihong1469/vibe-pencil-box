// <copyright file="DeviceLogClassifier.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Text.RegularExpressions;

namespace PencilBox.Core.SerialScope;

/// <summary>文本行 → 域标签（05 §3.2 / §3.3：设备日志分域，供过滤/着色/`grep`）</summary>
public interface ILogClassifier
{
    /// <summary>判定一行属于哪个域（返回值取 <see cref="LogDomain"/> 常量）</summary>
    string Classify(string rawLine);
}

/// <summary>
/// 设备日志分域器（纯逻辑）。
///
/// 依据**实测设备日志形态**（`serial-chat/logs/ota-backup/*`，07 §8）定规则，两条依次生效：
/// 1. **非括号的 `域:` 前缀** —— 真实域优先于标记：
///    `[DONGLE][TODO] ota_usb: dual_bank_update_allow_check(size=229298)` ⇒ `ota_usb`
///    （行内既有 `[TODO]` 又有 `ota_usb:`，按"域"归，不按"标记"归）
/// 2. **括号内已知域** —— `[DONGLE][f5] rx cmdtype=0x16 …` ⇒ `f5`；
///    `[GATT_CLIENT]remoter_name:…` ⇒ `gatt_client`
///
/// 其余：含 `[TODO]`/`[STUB]` ⇒ <see cref="LogDomain.Todo"/>；无法归类 ⇒ <see cref="LogDomain.Other"/>
/// （JTAG 二进制片段 `36 8B D7 AC …` 与 `^` 记号**天然**落在这里，05 §5.4）。
/// </summary>
public sealed partial class DeviceLogClassifier : ILogClassifier
{
    /// <summary>参与分域的已知域（小写比对；**不含** `todo` —— 它是标记不是域）</summary>
    private static readonly HashSet<string> KnownDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        LogDomain.OtaUsb,
        LogDomain.F5,
        LogDomain.Pair,
        LogDomain.GattClient,
        LogDomain.Ble,
        LogDomain.Usb
    };

    public string Classify(string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
            return LogDomain.Other;

        var byPrefix = FindDomainPrefix(rawLine);
        if (byPrefix != null)
            return byPrefix;

        var byBracket = FindBracketedDomain(rawLine);
        if (byBracket != null)
            return byBracket;

        if (rawLine.Contains("[TODO]", StringComparison.OrdinalIgnoreCase) ||
            rawLine.Contains("[STUB]", StringComparison.OrdinalIgnoreCase))
            return LogDomain.Todo;

        return LogDomain.Other;
    }

    /// <summary>规则 1：非括号的 `域:` 前缀（如 `ota_usb: …`）</summary>
    private static string? FindDomainPrefix(string line)
    {
        foreach (Match m in DomainPrefixRegex().Matches(line))
        {
            var name = m.Groups["d"].Value;
            if (KnownDomains.Contains(name))
                return name.ToLowerInvariant();
        }
        return null;
    }

    /// <summary>规则 2：`[…]` 括号内首个已知域（如 `[f5]` / `[GATT_CLIENT]`）</summary>
    private static string? FindBracketedDomain(string line)
    {
        foreach (Match m in BracketTokenRegex().Matches(line))
        {
            var name = m.Groups["t"].Value.Trim();
            if (KnownDomains.Contains(name))
                return name.ToLowerInvariant();
        }
        return null;
    }

    /// <summary>`域:` 前缀：要求域名前是行首或空白/括号边界，避免命中 `remoter_name:` 这类普通字段</summary>
    [GeneratedRegex(@"(?:^|[\s\[\]])(?<d>[A-Za-z_][A-Za-z0-9_]*)\s*:")]
    private static partial Regex DomainPrefixRegex();

    /// <summary>括号 token（`p:i` 样式的时间戳 `[00:40:52.935]` 也会被取到，但不在已知域内，自然跳过）</summary>
    [GeneratedRegex(@"\[(?<t>[^\[\]]*)\]")]
    private static partial Regex BracketTokenRegex();
}
