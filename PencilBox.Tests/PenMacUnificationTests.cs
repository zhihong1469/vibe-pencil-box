// <copyright file="PenMacUnificationTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Ble;
using PencilBox.Core.Communication;

namespace PencilBox.Tests;

/// <summary>
/// 笔端 MAC **统一**相关测试：
/// - 广播名尾号两种宽度（6 HEX 旧口径 / 12 HEX 新口径）解析与拼接（大小端一律经 <see cref="MacAddress"/>）；
/// - 名字尾号 vs 广播地址（权威）的一致性校验；
/// - **两条来源、一份存储**：BLE 扫描与串口 `0x51` 读都汇入同一份 <see cref="KnownPen"/> 目录。
/// </summary>
public class PenMacUnificationTests
{
    private static readonly byte[] PenMacDisplay = { 0xC6, 0x56, 0x00, 0x3A, 0x5D, 0xF1 };

    // ================= 广播名尾号（6 / 12 HEX） =================

    [Fact]
    public void TryGetMacSuffix_AcceptsBothWidths()
    {
        Assert.Equal("028478", BleNaming.TryGetMacSuffix("SunVote-R1:028478"));              // 旧口径：末 3 字节
        Assert.Equal("C656003A5DF1", BleNaming.TryGetMacSuffix("SunVote-R1:C656003A5DF1"));  // 新口径：全 6 字节
    }

    [Theory]
    [InlineData("SunVote-R1#")]          // 配对名（无分隔符）
    [InlineData("SunVote-R1")]           // 无尾号
    [InlineData("midea")]                // 非本产品
    [InlineData("SunVote-R1:0284")]      // 宽度不合法（4）
    [InlineData("SunVote-R1:0284789")]   // 宽度不合法（7）
    [InlineData(null)]
    public void TryGetMacSuffix_RejectsOthers(string? name)
    {
        Assert.Null(BleNaming.TryGetMacSuffix(name));
    }

    [Fact]
    public void TryGetFullMacFromName_OnlyFor12Hex()
    {
        Assert.Equal(PenMacDisplay, BleNaming.TryGetFullMacFromName("SunVote-R1:C656003A5DF1"));
        Assert.Null(BleNaming.TryGetFullMacFromName("SunVote-R1:3A5DF1"));   // 只有末 3 字节，无法还原完整 MAC
    }

    [Fact]
    public void BuildBtName_SelfAssembledFromDisplayMac()
    {
        // PC 自行拼接（笔端 08 决议 D6）：与笔端 0x51 实测读回值逐字节一致
        Assert.Equal("SunVote-R1:C656003A5DF1", BleNaming.BuildBtName(PenMacDisplay));
    }

    [Theory]
    [InlineData("SunVote-R1:C656003A5DF1", true)]    // 全尾号一致
    [InlineData("SunVote-R1:3A5DF1", true)]          // 末 3 字节一致
    [InlineData("SunVote-R1:FFFFFF", false)]         // 末 3 字节不一致
    [InlineData("SunVote-R1:FFFFFFFFFFFF", false)]   // 全尾号不一致
    public void IsMacSuffixConsistent_ComparedAgainstAuthoritativeAddress(string name, bool expected)
    {
        Assert.Equal(expected, BleNaming.IsMacSuffixConsistent(name, PenMacDisplay));
    }

    [Fact]
    public void IsMacSuffixConsistent_ReturnsNullWhenUndecidable()
    {
        Assert.Null(BleNaming.IsMacSuffixConsistent("SunVote-R1#", PenMacDisplay));
        Assert.Null(BleNaming.IsMacSuffixConsistent(null, PenMacDisplay));
    }

    // ================= 统一存储：两条来源 → 一份目录 =================

    [Fact]
    public void Upsert_AddsSerialSourcedPenIntoSharedCatalog()
    {
        var fromBle = Catalog(("AABBCCDDEEFF", PenMacSource.BleScan, 3));

        var merged = KnownPenCatalog.Upsert(fromBle, new KnownPen
        {
            MacDisplayHex = "C656003A5DF1",
            Source = PenMacSource.SerialRead,
            BtName = "SunVote-R1:C656003A5DF1"
        });

        Assert.Equal(2, merged.Count);   // 两条来源汇入同一份目录（互不覆盖）
        var serialPen = merged.Single(p => p.MacDisplayHex == "C656003A5DF1");
        Assert.Equal(PenMacSource.SerialRead, serialPen.Source);
        Assert.Equal("串口读取", serialPen.SourceName);
    }

    [Fact]
    public void Upsert_WhenSameMacFromAnotherSource_KeepsUsageHistory()
    {
        var existing = Catalog(("C656003A5DF1", PenMacSource.BleScan, 5));
        existing[0].FirstSeenUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var merged = KnownPenCatalog.Upsert(existing, new KnownPen
        {
            MacDisplayHex = "C656003A5DF1",
            Source = PenMacSource.SerialRead,
            BtName = "SunVote-R1:C656003A5DF1"
        });

        Assert.Single(merged);   // 同一 MAC 去重：不新增条目
        Assert.Equal(PenMacSource.SerialRead, merged[0].Source);   // 来源更新为最近一次
        Assert.Equal(5, merged[0].UseCount);                       // 使用统计保留（不被覆盖）
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), merged[0].FirstSeenUtc);
        Assert.Equal("SunVote-R1:C656003A5DF1", merged[0].BtName);
    }

    [Fact]
    public void KnownPen_DisplayOrderIsSingleSource_NoLinkOrderNeeded()
    {
        var pen = new KnownPen { MacDisplayHex = "C656003A5DF1", Source = PenMacSource.SerialRead };

        // 下发值 = 显示序原样（PC 零转换 —— 冻结基线 R9）
        Assert.Equal(PenMacDisplay, pen.MacDisplayOrder);
        Assert.Equal("C6:56:00:3A:5D:F1", pen.MacDisplayText);
    }

    [Fact]
    public void KnownPen_EffectiveBtName_FallsBackToSelfAssembly()
    {
        Assert.Equal("SunVote-R1:C656003A5DF1",
            new KnownPen { MacDisplayHex = "C656003A5DF1" }.EffectiveBtName);           // 未读到时自拼

        Assert.Equal("SunVote-R1:CUSTOM0001",
            new KnownPen { MacDisplayHex = "C656003A5DF1", BtName = "SunVote-R1:CUSTOM0001" }.EffectiveBtName);
    }

    // ================= 辅助 =================

    private static List<KnownPen> Catalog(params (string Mac, PenMacSource Source, int UseCount)[] items)
        => items.Select(i => new KnownPen
        {
            MacDisplayHex = i.Mac,
            Source = i.Source,
            UseCount = i.UseCount
        }).ToList();
}
