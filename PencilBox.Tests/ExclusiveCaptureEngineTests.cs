// <copyright file="ExclusiveCaptureEngineTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Infrastructure.Audio;

namespace PencilBox.Tests;

/// <summary>
/// 独占采集（t7 B-1）可测面：设备解析纯函数 + 候选格式表。
/// COM 采集循环本身走真机验证（Realtek 实测：独占 48k/2ch 落地、时钟失配 1.48×已入 [WARN] 自检；
/// UAC 声卡待在位后复验 16k 协商）。
/// 注：MatchDevice 泛型 T? 对值类型元组不产生 Nullable，缺省命中以 default(元组) 判定。
/// </summary>
public class ExclusiveCaptureEngineTests
{
    private static readonly (string Id, string Name)[] Devices =
    {
        ("{0.0.1.00000000}.{0e731588-b254-4228-a44d-5b0d3f3e0000}", "麦克风 (Realtek(R) Audio)"),
        ("{0.0.1.00000000}.{f434caf2-ece2-4f36-9a1b-3c8e5a1d0000}", "麦克风阵列 (UU远程虚拟音频设备)"),
        ("USB\\VID_1234&PID_5678\\abc", "Dongle (USB Audio 1.0)"),
    };

    private static readonly (string Id, string Name) None = default;

    [Fact]
    public void MatchDevice_ByFriendlyNameSubstring()
    {
        var hit = ExclusiveCaptureEngine.MatchDevice(Devices, d => d, "Realtek");
        Assert.NotEqual(None, hit);
        Assert.Contains("Realtek", hit.Name);
    }

    [Fact]
    public void MatchDevice_ByEndpointIdSubstring()
    {
        var hit = ExclusiveCaptureEngine.MatchDevice(Devices, d => d, "f434caf2");
        Assert.NotEqual(None, hit);
        Assert.Contains("UU", hit.Name);
    }

    [Fact]
    public void MatchDevice_CaseInsensitive()
    {
        Assert.NotEqual(None, ExclusiveCaptureEngine.MatchDevice(Devices, d => d, "realtek"));
        Assert.NotEqual(None, ExclusiveCaptureEngine.MatchDevice(Devices, d => d, "VID_1234"));
    }

    [Fact]
    public void MatchDevice_NoHit_ReturnsDefault()
    {
        Assert.Equal(None, ExclusiveCaptureEngine.MatchDevice(Devices, d => d, "不存在的设备"));
    }

    [Fact]
    public void MatchDevice_BlankSpec_ReturnsDefault()
    {
        Assert.Equal(None, ExclusiveCaptureEngine.MatchDevice(Devices, d => d, ""));
        Assert.Equal(None, ExclusiveCaptureEngine.MatchDevice(Devices, d => d, null));
    }

    [Fact]
    public void CandidateFormats_16kFirst_TargetsT7Baseline()
    {
        Assert.Equal(16000, ExclusiveCaptureEngine.CandidateFormats[0].SampleRate);
        Assert.Equal(1, ExclusiveCaptureEngine.CandidateFormats[0].Channels);
        Assert.All(ExclusiveCaptureEngine.CandidateFormats, f => Assert.Equal(16, f.BitsPerSample));
    }
}
