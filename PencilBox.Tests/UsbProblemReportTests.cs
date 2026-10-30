// <copyright file="UsbProblemReportTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Communication;

namespace PencilBox.Tests;

/// <summary>
/// USB 设备层异常盘点（10-24 现场：插 6 台只识别 4 台——根因=3 台 Code=10 无法启动，
/// Windows 层没拉起来故无命令通道，软件从未去重）。纯格式化单测；WMI 取数端在 OtaCore UsbProblemProbe。
/// 实例 ID 样例沿用真机 PnP 快照（同序列号批量插拔现场）；MI 子接口/复合根两类都覆盖。
/// </summary>
public class UsbProblemReportTests
{
    private static UsbPnpInstance Root(string tail, int code) =>
        new($"USB\\VID_2F70&PID_EA11\\{tail}", code);

    private static UsbPnpInstance Child(string mi, string tail, int code) =>
        new($"USB\\VID_2F70&PID_EA11&{mi}\\{tail}", code);

    [Fact]
    public void CompositeChild_DetectedByMiToken()
    {
        Assert.True(UsbProblemReport.IsCompositeChild("USB\\VID_2F70&PID_EA11&MI_02\\8&4113003&0&0002"));
        Assert.False(UsbProblemReport.IsCompositeChild("USB\\VID_2F70&PID_EA11\\6&32831749&1&2"));
    }

    [Fact]
    public void Empty_PlugOrPowerHint()
    {
        var line = UsbProblemReport.DescribeLine(Array.Empty<UsbPnpInstance>());

        Assert.Contains("本产品 0 台", line);
        Assert.Contains("未插入", line);
    }

    [Fact]
    public void AllHealthy_TotalOnly_NoAdviceTail()
    {
        var line = UsbProblemReport.DescribeLine(new[]
        {
            Root("CD71134331353930", 0), Root("6&32831749&1&2", 0), Root("9&168367ac&0&3", 0),
        });

        Assert.Contains("本产品 3 台（0 台异常）", line);
        Assert.DoesNotContain("拔下重插", line);
    }

    [Fact]
    public void SixPluggedThreeFailedStart_ShapeGroupedWithAdvice()
    {
        var instances = new[]
        {
            Root("CD71134331353930", 0), Root("6&32831749&1&2", 0), Root("9&168367ac&0&3", 0),
            Root("6&32831749&1&3", 10), Root("7&1F9D7D9F&0&1", 10), Root("7&1F9D7D9F&0&3", 10),
        };

        var line = UsbProblemReport.DescribeLine(instances);

        Assert.Contains("本产品 6 台（3 台异常）", line);
        Assert.Contains("无法启动(Code10)×3", line);
        Assert.Contains("拔下重插或换 USB 口", line);
    }

    [Fact]
    public void HealthyRootButFailedChild_InterfacesReportedSeparately()
    {
        var instances = new[]
        {
            Root("CD71134331353930", 0),
            Child("MI_00", "8&4113003&0&0000", 28),
        };

        var line = UsbProblemReport.DescribeLine(instances);

        Assert.Contains("本产品 1 台（0 台异常）", line);
        Assert.Contains("另有 1 个接口子实例异常", line);
    }

    [Fact]
    public void UnknownProblemCode_ShownVerbatim()
    {
        var line = UsbProblemReport.DescribeLine(new[] { Root("X", 43) });

        Assert.Contains("异常(Code43)×1", line);
    }

    [Fact]
    public void RawLines_BadFirst_PortTokenVisible()
    {
        var lines = UsbProblemReport.RawLines(new[]
        {
            Root("CD71134331353930", 0),
            Root("7&1F9D7D9F&0&1", 10),
            Child("MI_02", "8&4113003&0&0002", 0),
        });

        Assert.Equal(4, lines.Count);
        Assert.StartsWith("USB 设备层全量", lines[0]);
        Assert.Contains("[无法启动(Code10)]", lines[1]);   // 异常台排前
        Assert.Contains("7&1F9D7D9F&0&1", lines[1]);       // 根实例尾段=USB 口路径标识
        // 同为健康：ordinal 序 '&'<'\\'，MI 子接口行在前、复合根在后
        Assert.Contains("MI_02", lines[2]);
        Assert.Contains("[正常]", lines[2]);
        Assert.Contains("[正常] USB\\VID_2F70&PID_EA11\\CD71134331353930", lines[3]);
    }

    [Fact]
    public void CodeText_KnownAndUnknown()
    {
        Assert.Equal("正常", UsbProblemReport.CodeText(0));
        Assert.Equal("无法启动(Code10)", UsbProblemReport.CodeText(10));
        Assert.Equal("未装驱动(Code28)", UsbProblemReport.CodeText(28));
        Assert.Equal("异常(Code43)", UsbProblemReport.CodeText(43));
    }

    [Fact]
    public void RestartTargets_OnlyBadCompositeRoots_SortedByCode()
    {
        var targets = UsbProblemReport.RestartTargets(new[]
        {
            Root("OK-1", 0),
            Root("BAD-28", 28),
            Root("BAD-10-A", 10),
            Root("BAD-10-B", 10),
            Child("MI_02", "8&4113003&0&0002", 28),   // 子接口异常不单独重置（重置的是整台）
        });

        Assert.Equal(3, targets.Count);
        Assert.Equal(new[] { "BAD-10-A", "BAD-10-B", "BAD-28" },
            targets.Select(t => t.DeviceId.Split('\\')[2]).ToArray());   // 码升序，无法启动在前
    }

    [Fact]
    public void RestartTargets_AllHealthy_Empty()
    {
        Assert.Empty(UsbProblemReport.RestartTargets(new[] { Root("OK-1", 0), Child("MI_00", "8&x&0&0000", 0) }));
    }
}
