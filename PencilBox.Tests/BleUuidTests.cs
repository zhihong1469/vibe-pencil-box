// <copyright file="BleUuidTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Ble;

namespace PencilBox.Tests;

public class BleUuidTests
{
    // ---- TryNormalize：16-bit SIG 短形 ----

    [Fact]
    public void Normalize_SigShort_ExpandsToBaseUuid()
    {
        Assert.True(BleUuid.TryNormalize("180F", out var a));
        Assert.Equal("0000180F-0000-1000-8000-00805F9B34FB", a);

        Assert.True(BleUuid.TryNormalize("0x2A19", out var b));
        Assert.Equal("00002A19-0000-1000-8000-00805F9B34FB", b);

        Assert.True(BleUuid.TryNormalize("2a19", out var c));
        Assert.Equal("00002A19-0000-1000-8000-00805F9B34FB", c);
    }

    // ---- TryNormalize：128-bit ----

    [Fact]
    public void Normalize_128Bit_WithDashes_Uppercases()
    {
        Assert.True(BleUuid.TryNormalize("64b617f6-01af-7dbc-054f-215a5e000151", out var n));
        Assert.Equal("64B617F6-01AF-7DBC-054F-215A5E000151", n);
    }

    [Fact]
    public void Normalize_128Bit_WithoutDashes_Formats()
    {
        Assert.True(BleUuid.TryNormalize("8ec90001f3154f609fb8838830daea50", out var n));
        Assert.Equal("8EC90001-F315-4F60-9FB8-838830DAEA50", n);
    }

    // ---- TryNormalize：非法输入 ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("XYZ")]
    [InlineData("123")]          // 非 4/32 位
    [InlineData("G0000000-0000-0000-0000-000000000000")] // 非 HEX 字符
    public void TryNormalize_Invalid_ReturnsFalse(string? text)
    {
        Assert.False(BleUuid.TryNormalize(text, out _));
    }

    // ---- Normalize：非法抛异常 ----

    [Fact]
    public void Normalize_Invalid_Throws()
    {
        Assert.Throws<ArgumentException>(() => BleUuid.Normalize("nope"));
    }

    // ---- ToDisplay：SIG 族折叠回短形 ----

    [Fact]
    public void ToDisplay_SigFamily_FoldsToShort()
    {
        Assert.Equal("0x180F", BleUuid.ToDisplay("0000180F-0000-1000-8000-00805F9B34FB"));
        Assert.Equal("0x2A19", BleUuid.ToDisplay("00002A19-0000-1000-8000-00805F9B34FB"));
    }

    [Fact]
    public void ToDisplay_Custom128_KeepsFullForm()
    {
        Assert.Equal(
            "64B617F6-01AF-7DBC-054F-215A5E000151",
            BleUuid.ToDisplay("64B617F6-01AF-7DBC-054F-215A5E000151"));
    }
}
