// <copyright file="OtaToolEngineTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
extern alias OtaCore;
using OtaCore::PencilBox.Core.OtaTool;
using OtaCore::PencilBox.Core.Protocol.Company;

namespace PencilBox.Tests;

/// <summary>
/// 外发小工具引擎的固件装载校验（外发口径：目标轨=包名前缀声明、尺寸上限、版本解析——无跳过校验入口）。
/// 纯装载/解析逻辑，不碰真机。
/// </summary>
public class OtaToolEngineTests
{
    private static readonly OtaToolEngine Engine = new();

    private static string WriteTempBin(byte[] data, string name)
    {
        var path = Path.Combine(Path.GetTempPath(), name);
        File.WriteAllBytes(path, data);
        return path;
    }

    [Fact]
    public void LoadFirmware_parses_name_version_and_crc()
    {
        var image = new byte[2048];
        new Random(7).NextBytes(image);
        var path = WriteTempBin(image, "R1D_1.0.2_rc.1_win_4f245b6.bin");

        var fw = Engine.LoadFirmware(path);

        Assert.Equal("1.0.2", fw.SverText);
        Assert.Equal((1, 0, 2), (fw.SverMajor, fw.SverMinor, fw.SverPatch));
        Assert.Equal(Crc16CcittFalse.Compute(image), fw.Crc16);
        Assert.Null(fw.Warning);
        Assert.Equal(image.Length, fw.Image.Length);
    }

    [Fact]
    public void LoadFirmware_accepts_pen_package()
    {
        // 目标端=用户显式选择（指挥官 10-21 晚裁决，不靠包名推断）；R1P_ 前缀只是合法包名，装载不再硬拒
        var path = WriteTempBin(new byte[1024], "R1P_1.0.1_win_380191b.bin");

        var fw = Engine.LoadFirmware(path);

        Assert.Null(fw.Warning);
        Assert.Equal("1.0.1", fw.SverText);
    }

    [Fact]
    public void LoadFirmware_prefix_does_not_decide_target()
    {
        // R1D_/R1P_ 均合法且无警告；升级对象由 CLI --target / GUI 升级端开关显式选择
        var r1d = Engine.LoadFirmware(WriteTempBin(new byte[1024], "R1D_1.0.2_rc.1_win_4f245b6.bin"));
        var r1p = Engine.LoadFirmware(WriteTempBin(new byte[1024], "R1P_0.13.21_release_gcc_e2466ab.bin"));

        Assert.Null(r1d.Warning);
        Assert.Null(r1p.Warning);
        Assert.Equal("1.0.2", r1d.SverText);
        Assert.Equal("0.13.21", r1p.SverText);
    }

    [Fact]
    public void LoadFirmware_unknown_prefix_warns_but_loads()
    {
        var path = WriteTempBin(new byte[1024], "XYZ_1.0.1_win_deadbeef.bin");

        var fw = Engine.LoadFirmware(path);

        Assert.NotNull(fw.Warning);
        Assert.Equal("1.0.1", fw.SverText);
    }

    [Fact]
    public void LoadFirmware_rejects_oversize()
    {
        var path = WriteTempBin(new byte[0x38000 + 1], "R1D_1.0.1_dev_win_oversize.bin");

        var ex = Assert.Throws<OtaFirmwareLoadException>(() => Engine.LoadFirmware(path));
        Assert.Contains("上限", ex.Message);
    }

    [Fact]
    public void LoadFirmware_sver_override_wins()
    {
        var path = WriteTempBin(new byte[1024], "R1D_1.0.1_dev_win_380191b.bin");

        var fw = Engine.LoadFirmware(path, "9.9.9");

        Assert.Equal("9.9.9", fw.SverText);
    }

    [Theory]
    [InlineData("1.0.2", 1, 0, 2)]
    [InlineData(" 1.0.2 ", 1, 0, 2)]
    [InlineData("10.20.30", 10, 20, 30)]
    public void ParseSver_accepts_three_numeric_parts(string text, byte m, byte mi, byte p)
    {
        Assert.Equal((m, mi, p), OtaToolEngine.ParseSver(text));
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1.0.x")]
    [InlineData("1..2")]
    [InlineData("")]
    public void ParseSver_rejects_malformed(string text)
    {
        Assert.Null(OtaToolEngine.ParseSver(text));
    }
}
