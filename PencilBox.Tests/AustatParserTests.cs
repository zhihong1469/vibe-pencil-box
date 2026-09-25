// <copyright file="AustatParserTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.SerialScope;

namespace PencilBox.Tests;

/// <summary>基站 [AUSTAT] 遥测行解析（D3 1s 汇总行；字段容错）</summary>
public class AustatParserTests
{
    private static readonly DateTimeOffset Ts = new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ParsesFullLine()
    {
        var ok = AustatParser.TryParseText(Ts, "DONGLE",
            "2026-09-24 10:00:00.001 [DONGLE][app] [AUSTAT] wl=486(240-1024)B cbuf_full=0 alloc=1 usb_full=2 jump=3 conceal(f/e/skip)=14/7/1",
            out var s);

        Assert.True(ok);
        Assert.Equal(486, s.WatermarkBytes);
        Assert.Equal(240, s.WatermarkMinBytes);
        Assert.Equal(1024, s.WatermarkMaxBytes);
        Assert.Equal(0, s.CbufFull);
        Assert.Equal(1, s.AllocFail);
        Assert.Equal(2, s.UsbFull);
        Assert.Equal(3, s.SeqJump);
        Assert.Equal(14, s.ConcealFrames);
        Assert.Equal(7, s.ConcealEvents);
        Assert.Equal(1, s.ConcealSkip);
        Assert.Equal("DONGLE", s.Port);
    }

    [Fact]
    public void ToleratesMissingFields()
    {
        var ok = AustatParser.TryParseText(Ts, "P", "[AUSTAT] wl=100(0-100)B", out var s);

        Assert.True(ok);
        Assert.Equal(100, s.WatermarkBytes);
        Assert.Equal(0, s.CbufFull);
        Assert.Equal(0, s.ConcealFrames);
    }

    [Fact]
    public void RejectsNonAustatLines()
    {
        Assert.False(AustatParser.TryParseText(Ts, "P", "some random log line", out _));
        Assert.False(AustatParser.TryParseText(Ts, "P", "[AUSTAT] broken wl=abc", out _));
    }
}
