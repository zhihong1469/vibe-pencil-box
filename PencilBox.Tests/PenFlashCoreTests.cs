// <copyright file="PenFlashCoreTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.PenFlash;
using Xunit;

namespace PencilBox.Tests;

/// <summary>
/// PenFlash 纯逻辑核心单测（量产保障/isp烧录/Pencil/02 号 §3.3）。
/// PASS 判定是灵魂（01 号验收 2：拔治具必须 FAIL，不存在假 PASS 路径）。
/// </summary>
public class PenFlashCoreTests
{
    // ---- PASS 判定（退出码 0 且输出含 Finished Code:0，二者缺一不可）----

    [Fact]
    public void IsPass_RequiresBothExitCodeAndFinishedMark()
    {
        var good = "...\"Status\":\"Finished\",\"Code\":0...";
        Assert.True(PenFlashCore.IsPass(0, good));
        Assert.False(PenFlashCore.IsPass(1, good));                 // 有终态字样但退出码非 0
        Assert.False(PenFlashCore.IsPass(0, "no mark at all"));     // 退出码 0 但无终态证据
        Assert.False(PenFlashCore.IsPass(-1001, ""));
    }

    [Fact]
    public void IsPass_ToleratesWhitespaceBetweenKeys()
    {
        // 厂商输出若在键间插入空白，字面匹配会漏判——宽松双条件兜底
        var spaced = "\"Status\":\"Finished\", \"Code\":0";
        Assert.True(PenFlashCore.IsPass(0, spaced));
    }

    [Fact]
    public void IsPass_FailSamples_FromRealErrorForms()
    {
        // 进不了 ISP（-1001）与串口占用（13）形态：绝不能判 PASS
        Assert.False(PenFlashCore.IsPass(-1001, "ASYN_SETUP error"));
        Assert.False(PenFlashCore.IsPass(13, "Permission denied"));
        Assert.False(PenFlashCore.IsPass(-1303, "\"Status\":\"Finished\",\"Code\":-1303"));
    }

    [Fact]
    public void IsProgressNoise_FiltersProgrammingLines_KeepsOthers()
    {
        Assert.True(PenFlashCore.IsProgressNoise("{\"Status\":\"Programming\",\"Address\":4096}"));
        Assert.False(PenFlashCore.IsProgressNoise("{\"Status\":\"Finished\",\"Code\":0}"));
        Assert.False(PenFlashCore.IsProgressNoise(null!));
        Assert.False(PenFlashCore.IsProgressNoise("error -1001"));
    }

    // ---- 口换算（ps1：COM20 → /dev/ttyS19）----

    [Theory]
    [InlineData("COM20", "/dev/ttyS19")]
    [InlineData("COM7", "/dev/ttyS6")]
    [InlineData("COM1", "/dev/ttyS0")]
    public void ConvertOmPort_SubtractsOne(string com, string expected) =>
        Assert.Equal(expected, PenFlashCore.ConvertOmPort(com));

    [Theory]
    [InlineData("USB-SERIAL CH340 (COM20)", "COM20")]
    [InlineData("USB 串口设备 (COM3)", "COM3")]
    [InlineData("no port here", null)]
    public void ExtractComName_ParsesPnpName(string pnp, string? expected) =>
        Assert.Equal(expected, PenFlashCore.ExtractComName(pnp));

    [Fact]
    public void PickDefaultPort_NeverGuessesAmongMultiple()
    {
        Assert.Equal("COM20", PenFlashCore.PickDefaultPort(new[] { "COM20" }));   // 唯一候选才默认
        Assert.Null(PenFlashCore.PickDefaultPort(new[] { "COM20", "COM21" }));    // 多候选禁止猜口（01 号验收 4）
        Assert.Null(PenFlashCore.PickDefaultPort(Array.Empty<string>()));
    }

    // ---- 固件名解析 ----

    [Fact]
    public void DescribeFirmware_ParsesVersionAndDate()
    {
        Assert.Equal("v0.13.21 (20261014)",
            PenFlashCore.DescribeFirmware("pen_r1_v10_v0.13.21_target_20261014_gcc.bin"));
        Assert.Equal("v1.0.1 (20261015)",
            PenFlashCore.DescribeFirmware("pen_r1_v10_v1.0.1_target_20261015_gcc.bin"));
    }

    [Fact]
    public void DescribeFirmware_FallsBackToFileName_WithoutExtension()
    {
        Assert.Equal("unknown_fw", PenFlashCore.DescribeFirmware("unknown_fw.bin"));
    }

    [Fact]
    public void ParseFirmwareVersion_ReturnsVersionToken()
    {
        Assert.Equal("v0.13.21", PenFlashCore.ParseFirmwareVersion("pen_r1_v10_v0.13.21_target_20261014_gcc.bin"));
        Assert.Null(PenFlashCore.ParseFirmwareVersion("junk.bin"));
    }

    [Fact]
    public void UniqueFirmwareCopyName_IsPortScoped()
    {
        Assert.Equal("fw_COM20.bin", PenFlashCore.UniqueFirmwareCopyName("COM20"));
        // 两口不同名——并发拷固件不互踩（02 号裁决 4-②）
        Assert.NotEqual(PenFlashCore.UniqueFirmwareCopyName("COM20"), PenFlashCore.UniqueFirmwareCopyName("COM21"));
    }

    // ---- 出货版默认选择（唯一匹配才默认；0 或多个 = 不猜）----

    [Fact]
    public void SelectShippingFirmware_DefaultsOnlyOnUniqueMatch()
    {
        var a = new FirmwareEntry(@"X:\a.bin", "pen_r1_v10_v0.13.21_target_20261014_gcc.bin", 1, DateTimeOffset.Now);
        var b = new FirmwareEntry(@"X:\b.bin", "pen_r1_v10_v1.0.1_target_20261015_gcc.bin", 1, DateTimeOffset.Now);

        Assert.Equal(a.Path, PenFlashCore.SelectShippingFirmware(new[] { a, b }, "v0.13.21"));
        Assert.Null(PenFlashCore.SelectShippingFirmware(new[] { a, b }, "v9.9.9"));   // 无匹配不猜
        Assert.Null(PenFlashCore.SelectShippingFirmware(new[] { a, b }, ""));          // 口径缺失不猜
        Assert.Null(PenFlashCore.SelectShippingFirmware(Array.Empty<FirmwareEntry>(), "v0.13.21"));
    }

    [Theory]
    [InlineData("v0.13.21", "v0.13.21")]
    [InlineData("出货版本：v0.13.21", "v0.13.21")]
    [InlineData("v0.13.21-rc.1", "v0.13.21-rc.1")]
    [InlineData("", null)]
    [InlineData("（还没写内容）", null)]
    public void NormalizeShippingVersion_ExtractsToken(string content, string? expected) =>
        Assert.Equal(expected, PenFlashCore.NormalizeShippingVersion(content));

    // ---- 错误码对照（照抄 ps1 表，FAIL 处置提示）----

    [Fact]
    public void DescribeErrorCode_CoversKeyCodes()
    {
        Assert.Contains("档位", PenFlashCore.DescribeErrorCode(-1001));   // 进不了 ISP 的处置提示（01 号验收 2）
        Assert.Contains("占用", PenFlashCore.DescribeErrorCode(13));
        Assert.Equal("成功", PenFlashCore.DescribeErrorCode(0));
        Assert.StartsWith("未知", PenFlashCore.DescribeErrorCode(-9999));
    }

    // ---- 工具 JSON 码 + 退出码低 8 位反查（10-23 真机实证：内部 -1001 → 退出 23）----

    [Fact]
    public void ParseToolJsonCode_TakesLastJson()
    {
        var output = "{\"Device\":\"/dev/ttyS11\",\"Status\":\"Fail\",\"Code\":-1001,\"Message\":\"Link established fail\"}\r\n" +
                     "OnMicro BLE ISP Toolkit, TY1.2.3\r\n" +
                     "{\"Status\":\"Finished\",\"Code\":0}";
        Assert.Equal(0, PenFlashCore.ParseToolJsonCode(output));     // 最后一个 JSON 作数
        Assert.Equal(-1001, PenFlashCore.ParseToolJsonCode("{\"Status\":\"Fail\",\"Code\":-1001}"));
        Assert.Null(PenFlashCore.ParseToolJsonCode("no json here"));
        Assert.Null(PenFlashCore.ParseToolJsonCode(null));
    }

    [Fact]
    public void DescribeErrorCode_PrefersToolJsonOverExitCode()
    {
        // 真机 10-23 形态：退出码 23（=-1001 低 8 位），JSON 自报 -1001
        var output = "{\"Status\":\"Fail\",\"Code\":-1001,\"Message\":\"Link established fail\"}";
        Assert.Contains("通信链路未建立", PenFlashCore.DescribeErrorCode(23, output));
        Assert.Contains("-1001", PenFlashCore.DescribeErrorCode(23, output));
    }

    [Fact]
    public void DescribeErrorCode_ReverseMapsExitByte_WhenNoJson()
    {
        Assert.Contains("通信链路未建立", PenFlashCore.DescribeErrorCode(23));   // -1001 & 0xFF = 23
        Assert.Contains("芯片无响应", PenFlashCore.DescribeErrorCode(77));       // -1203 & 0xFF = 77
        Assert.Contains("占用", PenFlashCore.DescribeErrorCode(13));             // 正码 errno 直查不被反查劫走
        Assert.StartsWith("未知", PenFlashCore.DescribeErrorCode(100));          // 查不到不猜
    }

    [Fact]
    public void IsLinkSetupFailure_Family()
    {
        Assert.True(PenFlashCore.IsLinkSetupFailure(-1001));
        Assert.True(PenFlashCore.IsLinkSetupFailure(-1203));
        Assert.True(PenFlashCore.IsLinkSetupFailure(-1007));
        Assert.False(PenFlashCore.IsLinkSetupFailure(-1303));
        Assert.False(PenFlashCore.IsLinkSetupFailure(null));
    }
}
