// <copyright file="ScanDiagnosticTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Communication;

namespace PencilBox.Tests;

/// <summary>
/// 扫描诊断行（10-24 现场：插 6 台只显 4 台——扫描/分组无数量上限，把枚举漏斗摊开定位差额层）。
/// VID/PID 样例沿用真机枚举 vid_2f70&amp;pid_ea11。
/// </summary>
public class ScanDiagnosticTests
{
    private static HidDeviceInfo Dev(bool commandChannel, ushort vid = 0x2F70, ushort pid = 0xEA11) => new()
    {
        DevicePath = $"\\\\?\\hid#vid_{vid:x4}&pid_{pid:x4}&mi_02#9&{Guid.NewGuid():n}&0&0000#{{4d1e55b2-f16f-11cf-88cb-001111000030}}",
        VendorId = vid,
        ProductId = pid,
        IsOurDevice = true,
        IsCommandChannel = commandChannel,
    };

    [Fact]
    public void SixPlugged_TwoWithoutCommandChannel_MismatchIsVisible()
    {
        var devices = Enumerable.Range(0, 6).Select(i => Dev(commandChannel: i < 4)).ToList();

        var line = HidDeviceSelection.ScanDiagnosticLine(devices);

        Assert.Contains("HID 实例 6 个", line);
        Assert.Contains("本产品 VID/PID 6 个", line);
        Assert.Contains("0xFF02 命令通道 4 个", line);
        Assert.Contains("另 2 个本产品实例未开命令通道", line);
    }

    [Fact]
    public void AllMatched_NoWarningTail()
    {
        var devices = Enumerable.Range(0, 4).Select(_ => Dev(commandChannel: true)).ToList();

        var line = HidDeviceSelection.ScanDiagnosticLine(devices);

        Assert.Contains("0xFF02 命令通道 4 个", line);
        Assert.DoesNotContain("未开命令通道", line);
    }

    [Fact]
    public void ForeignDevicesNotCounted_ZeroOursHints()
    {
        var noise = Enumerable.Range(0, 3).Select(_ => Dev(true, vid: 0x1234, pid: 0x5678)).ToList();
        var line = HidDeviceSelection.ScanDiagnosticLine(noise);
        Assert.Contains("HID 实例 3 个", line);
        Assert.Contains("本产品 VID/PID 0 个", line);
        Assert.Contains("未发现本产品设备", line);

        var mixed = noise.Append(Dev(true)).ToList();
        var mixedLine = HidDeviceSelection.ScanDiagnosticLine(mixed);
        Assert.Contains("HID 实例 4 个", mixedLine);
        Assert.Contains("本产品 VID/PID 1 个", mixedLine);
        Assert.DoesNotContain("未开命令通道", mixedLine);
    }
}
