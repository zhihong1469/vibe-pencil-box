// <copyright file="BleCommands.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Cli.Output;
using PencilBox.Core.Communication;

namespace PencilBox.Cli.Commands;

/// <summary>
/// BLE 广播扫描命令域（AI 调试首选零 UI）。用于识别/推荐 SunVote 笔端目标 MAC，免手输给 Dongle。
/// 底层识别/推荐算法在 Core（BleNaming/MacAddress），本类只做 IO 与展示。
/// </summary>
public class BleCommands
{
    private readonly IBleScanner _bleScanner;

    public BleCommands(IBleScanner bleScanner)
    {
        _bleScanner = bleScanner;
    }

    public async Task<int> ScanAsync(int duration)
    {
        ConsoleFormatter.Info($"Scanning BLE for {duration}s...");
        var devices = await _bleScanner.ScanAsync(duration);

        var list = devices.ToList();
        ConsoleFormatter.Info($"Found {list.Count} BLE device(s)");

        ConsoleFormatter.PrintTable(list,
            ("Pentag", d => d.IsSunVotePen ? "★" : ""),
            ("MAC", d => d.MacDisplayText),
            ("Name", d => d.LocalName ?? "-"),
            ("RSSI", d => d.Rssi.ToString()),
            ("Mode", d => d.IsPairingMode ? "PAIR" : (d.IsSunVotePen ? "NORM" : "-")),
            ("Link6B", d => d.IsSunVotePen ? d.MacLinkText : "-"));

        return 0;
    }

    public async Task<int> RecommendAsync(int duration)
    {
        ConsoleFormatter.Info($"Scanning BLE for {duration}s...");
        var devices = await _bleScanner.ScanAsync(duration);

        var penList = devices.Where(d => d.IsSunVotePen).ToList();
        if (penList.Count == 0)
        {
            ConsoleFormatter.Info("No SunVote pen broadcast detected");
            return 1;
        }

        ConsoleFormatter.Info($"SunVote pen detected: {penList.Count} (sorted by priority)");
        var sorted = penList
            .OrderByDescending(d => d.IsPairingMode)
            .ThenByDescending(d => d.Rssi);

        foreach (var d in sorted)
        {
            var mark = d.IsPairingMode ? " [PAIR: 首选]" : " [NORM]";
            ConsoleFormatter.Info($"  {d.MacDisplayText} rssi={d.Rssi}dBm \"{d.LocalName}\"{mark}");
            ConsoleFormatter.Info($"    链路序(供A4/6B): {d.MacLinkText}");
        }

        var best = sorted.First();
        ConsoleFormatter.Success($"推荐目标(链路序): {best.MacLinkText}  ←  用于 `device ep4 send --cmd A4` (Dongle 写 INFO3 / 裸6B注入)");
        return 0;
    }
}
