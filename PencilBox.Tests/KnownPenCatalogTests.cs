// <copyright file="KnownPenCatalogTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Ble;
using PencilBox.Core.Communication;

namespace PencilBox.Tests;

public class KnownPenCatalogTests
{
    private static BleDeviceInfo MakeScan(string macDisplay, string? name, int rssi, bool pairing = false)
    {
        var display = MacAddress.ParseDisplay(macDisplay);
        return new BleDeviceInfo
        {
            MacDisplayOrder = display,
            MacLinkOrder = MacAddress.ToLinkOrder(display),
            LocalName = name,
            Rssi = rssi,
            IsSunVotePen = BleNaming.IsSunVotePen(name),
            IsPairingMode = pairing
        };
    }

    [Fact]
    public void Merge_AddsNewPenAndDedupsByMac()
    {
        var existing = new List<KnownPen>();
        var scans = new[]
        {
            MakeScan("AA:BB:CC:DD:EE:01", "SunVote-R1:000001", -30),
            MakeScan("AA:BB:CC:DD:EE:02", "SunVote-R1#", -50, pairing: true)
        };

        var merged = KnownPenCatalog.Merge(existing, scans);

        Assert.Equal(2, merged.Count);
        Assert.True(merged.All(p => p.IsPresent));
        Assert.Contains(merged, p => p.MacDisplayText == "AA:BB:CC:DD:EE:01");
        Assert.Contains(merged, p => p.IsPairing);
    }

    [Fact]
    public void Merge_ExistingEntry_UpdatesRssiNotDuplicate()
    {
        var existing = KnownPenCatalog.Merge(new List<KnownPen>(), new[]
        {
            MakeScan("AA:BB:CC:DD:EE:01", "SunVote-R1:000001", -40)
        });

        var merged = KnownPenCatalog.Merge(existing, new[]
        {
            MakeScan("AA:BB:CC:DD:EE:01", "SunVote-R1:000001", -20)   // 更强信号
        });

        Assert.Single(merged);
        Assert.Equal(-20, merged[0].Rssi);
    }

    [Fact]
    public void AssignLevels_Orders_LastUsed_Gt_Used_Gt_Seen()
    {
        var list = new List<KnownPen>
        {
            new() { MacDisplayHex = "AABBCCDDEE01", LastUsedUtc = null },                                  // Seen
            new() { MacDisplayHex = "AABBCCDDEE02", LastUsedUtc = DateTime.UtcNow.AddMinutes(-10) },        // Used(旧)
            new() { MacDisplayHex = "AABBCCDDEE03", LastUsedUtc = DateTime.UtcNow }                         // LastUsed(新)
        };

        var ordered = KnownPenCatalog.AssignLevels(list);

        Assert.Equal(PenUsageLevel.LastUsed, ordered[0].Level);
        Assert.Equal(PenUsageLevel.Used, ordered[1].Level);
        Assert.Equal(PenUsageLevel.Seen, ordered[2].Level);
        Assert.Equal("AABBCCDDEE03", ordered[0].MacDisplayHex);
    }

    [Fact]
    public void AssignLevels_WithinSameLevel_PrefersPresentAndStrongerRssi()
    {
        var list = new List<KnownPen>
        {
            new() { MacDisplayHex = "AABBCCDDEE01", Rssi = -60, IsPresent = false },
            new() { MacDisplayHex = "AABBCCDDEE02", Rssi = -70, IsPresent = true },
            new() { MacDisplayHex = "AABBCCDDEE03", Rssi = -30, IsPresent = true }
        };

        var ordered = KnownPenCatalog.AssignLevels(list);

        Assert.Equal(PenUsageLevel.Seen, ordered[0].Level);
        Assert.True(ordered[0].IsPresent);              // 在场优先
        Assert.Equal("AABBCCDDEE03", ordered[0].MacDisplayHex); // 在场 + 信号强
    }
}
