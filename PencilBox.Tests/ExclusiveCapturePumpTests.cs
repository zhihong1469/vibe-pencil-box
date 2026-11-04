// <copyright file="ExclusiveCapturePumpTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using NAudio.Wave;
using PencilBox.Infrastructure.Audio;

namespace PencilBox.Tests;

/// <summary>
/// 独占采集泵（扩音一键独占，2026-11-05）可测面：候选协商纯函数 + 失败归因。
/// COM 采集循环/独占初始化本身走真机验证（t7 B-1 配方：Realtek 48k/2ch、UAC 16k 均已实测）。
/// </summary>
public class ExclusiveCapturePumpTests
{
    private static readonly WaveFormat Requested16k = new(16000, 16, 1);
    private static readonly WaveFormat[] Fallbacks = ExclusiveCaptureEngine.CandidateFormats;

    [Fact]
    public void SelectFormat_RequestedFirst()
    {
        // UAC 麦原生 16k/mono（t7 真验口径）：请求格式应最先命中
        var hit = ExclusiveCapturePump.SelectFormat(Requested16k, Fallbacks,
            f => f.SampleRate == 16000 && f.Channels == 1, out var reason);
        Assert.NotNull(hit);
        Assert.Equal(16000, hit!.SampleRate);
        Assert.Equal(1, hit.Channels);
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void SelectFormat_FallsBackInOrder()
    {
        // 请求格式不支持 → 按回退表顺序取第一个支持的（16k/2ch 在 48k 之前）
        var supported = new[] { "16000/16/2", "48000/16/1" };
        var hit = ExclusiveCapturePump.SelectFormat(Requested16k, Fallbacks,
            f => supported.Contains($"{f.SampleRate}/{f.BitsPerSample}/{f.Channels}"), out _);
        Assert.NotNull(hit);
        Assert.Equal(16000, hit!.SampleRate);
        Assert.Equal(2, hit.Channels);
    }

    [Fact]
    public void SelectFormat_DedupesRequestedAgainstFallbacks()
    {
        // 请求格式与回退表首项相同（16k/16/1 也在 CandidateFormats 里）：探测器只应被问一次
        var probes = 0;
        var hit = ExclusiveCapturePump.SelectFormat(Requested16k, Fallbacks,
            _ => { probes++; return true; }, out _);
        Assert.NotNull(hit);
        Assert.Equal(1, probes);
    }

    [Fact]
    public void SelectFormat_NoneSupported_ReportsCandidates()
    {
        var hit = ExclusiveCapturePump.SelectFormat(Requested16k, Fallbacks, _ => false, out var reason);
        Assert.Null(hit);
        Assert.Contains("不支持候选格式", reason);
        Assert.Contains("16000Hz/16bit/1ch", reason);   // 请求格式在列（报实情不硬猜，t7 口径）
    }

    [Fact]
    public void DescribeExclusiveFailure_DeviceInUse_MapsToOccupyHint()
    {
        var ex = new System.Runtime.InteropServices.COMException("busy",
            ExclusiveCapturePump.AudclntEDeviceInUse);
        var text = ExclusiveCapturePump.DescribeExclusiveFailure(ex);
        Assert.Contains("占用", text);
        Assert.Contains("互斥", text);
    }

    [Fact]
    public void DescribeExclusiveFailure_Generic_KeepsMessage()
    {
        var text = ExclusiveCapturePump.DescribeExclusiveFailure(new InvalidOperationException("格式怪癖"));
        Assert.Contains("格式怪癖", text);
    }
}
