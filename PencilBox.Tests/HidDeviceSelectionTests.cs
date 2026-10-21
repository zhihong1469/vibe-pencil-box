// <copyright file="HidDeviceSelectionTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Communication;

namespace PencilBox.Tests;

/// <summary>
/// 多实例差异化 + 设备体系分组（02 号多笔同升 / 10-21 同号双台实证）。
/// 路径样例取自真机枚举：`\\?\hid#vid_2f70&amp;pid_ea11&amp;mi_02#9&amp;168367ac&amp;0&amp;0000#{GUID}`。
/// </summary>
public class HidDeviceSelectionTests
{
    private const string GuidSeg = "#{4d1e55b2-f16f-11cf-88cb-001111000030}";

    private static HidDeviceInfo Dev(string instanceSegment, string sn = "", bool commandChannel = true) => new()
    {
        DevicePath = $"\\\\?\\hid#vid_2f70&pid_ea11&mi_02#{instanceSegment}{GuidSeg}",
        SerialNumber = sn,
        UsagePage = 0xFF02,
        VendorId = 0x2F70,
        ProductId = 0xEA11,
        IsOurDevice = true,
        IsTargetDongle = true,
        IsCommandChannel = commandChannel,
    };

    [Fact]
    public void PathToken_extracts_instance_segment()
    {
        var d = Dev("9&168367ac&0&0000", sn: "CD70134330363235");
        Assert.Equal("9&168367ac&0&0000", HidDeviceSelection.PathToken(d));
    }

    [Fact]
    public void DeviceGroupKey_strips_collection_tail()
    {
        var anchor = Dev("9&168367ac&0&0000");
        var keyboard = new HidDeviceInfo
        {
            DevicePath = $"\\\\?\\hid#vid_2f70&pid_ea11&mi_03#9&168367ac&0&0001{GuidSeg}",
            IsOurDevice = true,
            InputReportLength = 9,
            OutputReportLength = 2,
        };
        Assert.Equal("9&168367ac&0", HidDeviceSelection.DeviceGroupKey(anchor));
        Assert.Equal(HidDeviceSelection.DeviceGroupKey(anchor), HidDeviceSelection.DeviceGroupKey(keyboard));
    }

    [Fact]
    public void DeviceGroups_one_row_per_dongle_with_all_channels()
    {
        var cmd = Dev("9&168367ac&0&0000", sn: "CD70134330363235");
        var kbd = new HidDeviceInfo
        {
            DevicePath = $"\\\\?\\hid#vid_2f70&pid_ea11&mi_03#9&168367ac&0&0001{GuidSeg}",
            IsOurDevice = true,
        };
        var other = Dev("9&2d4f1a3b&0&0000", sn: "CD71134331353930");

        var groups = HidDeviceSelection.DeviceGroups(new[] { cmd, kbd, other });

        Assert.Equal(2, groups.Count);
        Assert.Equal(cmd, groups[0].CommandChannel);   // 锚=命令通道
        Assert.Equal(2, groups[0].Channels.Count);     // 同台：命令通道 + 键盘
        Assert.Single(groups[1].Channels);
    }

    [Fact]
    public void DeviceGroups_same_serial_two_dongles_do_not_merge()
    {
        // 10-21 实测：两台 dongle 软件读序列号同值——分组键走 USB 实例主体，不串台
        var a = Dev("9&168367ac&0&0000", sn: "CD71134331353930");
        var b = Dev("9&2d4f1a3b&0&0000", sn: "CD71134331353930");

        var groups = HidDeviceSelection.DeviceGroups(new[] { a, b });

        Assert.Equal(2, groups.Count);
        Assert.Equal("9&168367ac&0", groups[0].Key);
        Assert.Equal("9&2d4f1a3b&0", groups[1].Key);
    }

    [Fact]
    public void Select_by_path_fragment_resolves_same_serial_dongles()
    {
        var a = Dev("9&168367ac&0&0000", sn: "CD71134331353930");
        var b = Dev("9&2d4f1a3b&0&0000", sn: "CD71134331353930");

        var selection = HidDeviceSelection.Select(new[] { a, b }, "9&2d4f1a3b&0");

        Assert.Null(selection.Error);
        Assert.Same(b, selection.Selected);
    }
}
