using PencilBox.Core.Ble;
using PencilBox.Core.Communication;

namespace PencilBox.Tests;

public class BleNamingTests
{
    // ---- MacAddress 字节序 ----

    [Fact]
    public void MacAddress_ToLinkOrder_ReversesDisplay()
    {
        var display = MacAddress.ParseDisplay("C6:56:00:3A:4A:F1");
        var link = MacAddress.ToLinkOrder(display);
        // 链路序 = 显示序反转（box 测试需求 #1 口径：显示序 C6:56:00:3A:4A:F1 → 发 F1 4A 3A 00 56 C6）
        Assert.Equal(new byte[] { 0xF1, 0x4A, 0x3A, 0x00, 0x56, 0xC6 }, link);

        // 往返一致
        Assert.Equal(display, MacAddress.ToDisplayOrder(link));
    }

    [Fact]
    public void MacAddress_ParseDisplay_PremovesSeparators()
    {
        Assert.Equal(
            new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF },
            MacAddress.ParseDisplay("AA:BB:CC:DD:EE:FF"));
        Assert.Equal(
            new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF },
            MacAddress.ParseDisplay("AABBCCDDEEFF"));
        Assert.Equal("AA:BB:CC:DD:EE:FF", MacAddress.FormatDisplay(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF }));
    }

    [Fact]
    public void MacAddress_ParseDisplay_Invalid_Throws()
    {
        // 非 12 hex 长度 → ArgumentException；含非法 hex 字符 → FormatException
        Assert.Throws<ArgumentException>(() => MacAddress.ParseDisplay("AA:BB:CC"));
        Assert.ThrowsAny<Exception>(() => MacAddress.ParseDisplay("GG:HH:II:JJ:KK:LL"));
    }

    // ---- BleNaming 识别 ----

    [Fact]
    public void BleNaming_IsSunVotePen_TrueForPenNames()
    {
        Assert.True(BleNaming.IsSunVotePen("SunVote-R1:028478"));
        Assert.True(BleNaming.IsSunVotePen("SunVote-R1#"));
        Assert.False(BleNaming.IsSunVotePen("midea"));
        Assert.False(BleNaming.IsSunVotePen(null));
        Assert.False(BleNaming.IsSunVotePen(""));
    }

    [Fact]
    public void BleNaming_IsPairingMode_OnlyHashSuffix()
    {
        Assert.True(BleNaming.IsPairingMode("SunVote-R1#"));
        Assert.False(BleNaming.IsPairingMode("SunVote-R1:028478"));
        Assert.False(BleNaming.IsPairingMode("SunVote-R1"));
    }

    [Fact]
    public void BleNaming_TryGetMacSuffix_ParsesNormName()
    {
        Assert.Equal("028478", BleNaming.TryGetMacSuffix("SunVote-R1:028478"));
        Assert.Null(BleNaming.TryGetMacSuffix("SunVote-R1#"));
        Assert.Null(BleNaming.TryGetMacSuffix("midea"));
        Assert.Null(BleNaming.TryGetMacSuffix("SunVote-R1"));
    }

    // ---- 推荐（配对态优先 + RSSI 降序）----

    private static BleDeviceInfo MakePen(string name, int rssi) => new()
    {
        LocalName = name,
        IsSunVotePen = BleNaming.IsSunVotePen(name),
        IsPairingMode = BleNaming.IsPairingMode(name),
        Rssi = rssi
    };

    [Fact]
    public void BleNaming_Recommend_PrefersPairingModeOverNorm()
    {
        var devices = new[]
        {
            MakePen("SunVote-R1:028478", -30),   // 常态，信号强
            MakePen("SunVote-R1#", -60)          // 配对态，信号弱
        };

        var best = BleNaming.Recommend(devices);
        Assert.NotNull(best);
        Assert.True(best!.IsPairingMode);         // 配对态优先
    }

    [Fact]
    public void BleNaming_Recommend_WithinSameMode_StrongestRssiWins()
    {
        var devices = new[]
        {
            MakePen("SunVote-R1:028478", -50),
            MakePen("SunVote-R1:ABCDEF", -20)
        };

        var best = BleNaming.Recommend(devices);
        Assert.NotNull(best);
        Assert.Equal("SunVote-R1:ABCDEF", best!.LocalName);   // 信号最强
    }

    [Fact]
    public void BleNaming_Recommend_IgnoresNonSunVote()
    {
        var devices = new[]
        {
            MakePen("midea", -80),
            MakePen("SomeOther", -70)
        };

        Assert.Null(BleNaming.Recommend(devices));
    }
}
