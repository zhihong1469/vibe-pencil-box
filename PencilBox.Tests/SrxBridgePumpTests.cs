// <copyright file="SrxBridgePumpTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Infrastructure.Audio;

namespace PencilBox.Tests;

/// <summary>虚拟声卡桥纯函数单测：配对发现/名字判据/PCM 转换/抽取。</summary>
public class SrxBridgePumpTests
{
    [Fact]
    public void SelectPair_VbCable_PairsInputRenderWithOutputCapture()
    {
        var renders = new[] { "Realtek Speakers", "CABLE Input (VB-Audio Virtual Cable)" };
        var captures = new[] { "麦克风 (Realtek)", "CABLE Output (VB-Audio Virtual Cable)" };
        var (r, c) = SrxBridgePump.SelectPair(renders, captures);
        Assert.Equal(1, r);
        Assert.Equal(1, c);
    }

    [Fact]
    public void SelectPair_NoVirtual_ReturnsMinusOne()
    {
        var (r, c) = SrxBridgePump.SelectPair(
            new[] { "Realtek Speakers", "NVIDIA Output" },
            new[] { "麦克风 (Realtek)" });
        Assert.Equal(-1, r);
        Assert.Equal(-1, c);
    }

    [Fact]
    public void SelectPair_BaseMismatch_DoesNotPair()
    {
        // 渲染命中虚拟但词根不同（Cable-A vs Cable-B）不配对
        var (r, c) = SrxBridgePump.SelectPair(
            new[] { "Cable-A Input" },
            new[] { "Cable-B Output" });
        Assert.Equal(-1, r);
        Assert.Equal(-1, c);
    }

    [Fact]
    public void SelectPair_ParenFamilyPairs_ChineseDriverStyle()
    {
        // 中文驱动形态：扬声器/麦克风前缀不同但括注同族 → 配对（本机 UU 虚拟声卡实型）
        var (r, c) = SrxBridgePump.SelectPair(
            new[] { "扬声器 (UU远程虚拟音频设备)", "扬声器 (Realtek(R) Audio)" },
            new[] { "麦克风 (Realtek)", "麦克风 (UU远程虚拟音频设备)" });
        Assert.Equal(0, r);
        Assert.Equal(1, c);
    }

    [Theory]
    [InlineData("CABLE Input", true)]
    [InlineData("Voicemeeter Input", true)]
    [InlineData("UU虚拟声卡", true)]
    [InlineData("Realtek Speakers", false)]
    public void IsVirtualDeviceName_KeywordHits(string name, bool expected) =>
        Assert.Equal(expected, SrxBridgePump.IsVirtualDeviceName(name));

    [Theory]
    [InlineData("CABLE Input (VB-Audio Virtual Cable)", "CABLE")]
    [InlineData("CABLE Output (VB-Audio Virtual Cable)", "CABLE")]
    [InlineData("Voicemeeter Input", "Voicemeeter")]
    public void NormalizeBaseName_StripsTailAndParens(string name, string expected) =>
        Assert.Equal(expected, SrxBridgePump.NormalizeBaseName(name));

    [Fact]
    public void Pcm16ToFloat_RoundTrip_KnownValues()
    {
        // 0 → 0f；32767 → ~1f；-32768 → -1f；小端字节序
        var bytes = new byte[] { 0x00, 0x00, 0xFF, 0x7F, 0x00, 0x80 };
        var f = SrxBridgePump.Pcm16ToFloat(bytes);
        Assert.Equal(3, f.Length);
        Assert.Equal(0f, f[0], 5);
        Assert.Equal(32767f / 32768f, f[1], 5);
        Assert.Equal(-1f, f[2], 5);
    }

    [Fact]
    public void DecimateTo16k_Ratio3_BoxAverage()
    {
        // 每 3 样本平均：[3,3,3]→3，[0,3,6]→9/3=3
        var d = SrxBridgePump.DecimateTo16k(new short[] { 3, 3, 3, 0, 3, 6 }, 3);
        Assert.Equal(new short[] { 3, 3 }, d);
    }

    [Fact]
    public void SelectPair_UuShellAndCable_PrefersNonUu()
    {
        // 指挥官 11-02 裁决：UU 远程外壳不作桥目标；同场有 CABLE 时必须选 CABLE
        var renders = new[] { "扬声器 (UU远程虚拟音频设备)", "CABLE Input (VB-Audio Virtual Cable)" };
        var captures = new[] { "麦克风 (UU远程虚拟音频设备)", "CABLE Output (VB-Audio Virtual Cable)" };
        var (ri, ci) = SrxBridgePump.SelectPair(renders, captures);
        Assert.Equal(1, ri);
        Assert.Equal(1, ci);
    }

    [Fact]
    public void SelectPair_UuShellOnly_StillReturnsButFlagged()
    {
        // 只有 UU 外壳时仍返回（调用方警告，不静默挂错）；IsUuShellName 可判
        var renders = new[] { "扬声器 (UU远程虚拟音频设备)", "Realtek Speakers" };
        var captures = new[] { "麦克风 (UU远程虚拟音频设备)", "Realtek Mic" };
        var (ri, ci) = SrxBridgePump.SelectPair(renders, captures);
        Assert.Equal(0, ri);
        Assert.Equal(0, ci);
        Assert.True(SrxBridgePump.IsUuShellName(renders[ri]));
        Assert.False(SrxBridgePump.IsUuShellName(renders[1]));
    }

    [Fact]
    public void ExtractMonoShort_Int16Stereo_TakesFirstChannel()
    {
        // 2 帧×2ch×2B：帧0=(1,-1) 帧1=(2,-2) → 取 ch0 = [1,2]
        var raw = new byte[]
        {
            0x01, 0x00, 0xFF, 0xFF, // 1, -1
            0x02, 0x00, 0xFE, 0xFF, // 2, -2
        };
        var fmt = new NAudio.Wave.WaveFormat(16000, 16, 2);
        var mono = SrxBridgePump.ExtractMonoShort(raw, fmt);
        Assert.Equal(new short[] { 1, 2 }, mono);
    }
}
