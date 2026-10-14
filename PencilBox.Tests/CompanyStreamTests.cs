// <copyright file="CompanyStreamTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Protocol.Company;

namespace PencilBox.Tests;

/// <summary>
/// `0x62` 空鼠遥测流（笔端 06 卷 + pc-issues 对齐卷 06）：codec 黄金向量 + 录制器丢流回绕 + CSV 格式。
///
/// 字节口径：21B 流帧全大端（seq u16 / flags u8 / 六轴 i16 / rate 双 i16 / out 双 i8）；
/// START/STOP 应答载荷 ≤3B（子命令回显 + STATUS[+divider]）。单位定案（对齐卷 06 §1）：
/// 陀螺 raw=0.01dps、加速度 raw=0.001g（定点直出，原"LSB 原码+FS 系数"提案作废，饱和非回绕）。
/// </summary>
public class CompanyStreamTests
{
    private readonly CompanyCodec _codec = new();

    // ================= 0x62 编码（黄金向量） =================

    [Fact]
    public void EncodeStreamStart_GoldenVector_Div2()
    {
        // START div=2（25Hz）：BASECMD=0x61（下行恒值）+ CMDTYPE=0x62 + DATA=[01][div]
        // → `F5 AA AA 05 61 01 62 01 02`（LEN=3+2=5）
        var frame = _codec.EncodeStreamStart(2);
        Assert.Equal(9, frame.Length);
        Assert.Equal(
            new byte[] { 0xF5, 0xAA, 0xAA, 0x05, 0x61, 0x01, 0x62, 0x01, 0x02 },
            frame);
    }

    [Fact]
    public void EncodeStreamStop_GoldenVector()
    {
        // STOP：DATA=[00] → `F5 AA AA 04 61 01 62 00`（LEN=3+1=4）
        var frame = _codec.EncodeStreamStop();
        Assert.Equal(8, frame.Length);
        Assert.Equal(
            new byte[] { 0xF5, 0xAA, 0xAA, 0x04, 0x61, 0x01, 0x62, 0x00 },
            frame);
    }

    // ================= 0x62 应答解析（子命令回显 + 载荷长判别） =================

    [Fact]
    public void TryParseStreamResp_Start_EchoesDivider()
    {
        // START 应答：[01][STATUS=00][echo div=2]
        Assert.True(_codec.TryParseStreamResp(new byte[] { 0x01, 0x00, 0x02 }, StreamCmd.Start,
            out var status, out var div));
        Assert.Equal(TuningStatus.Ok, status);
        Assert.Equal(2, div);

        // 子命令不匹配（把 STOP 应答喂给 START 等待）→ 拒收
        Assert.False(_codec.TryParseStreamResp(new byte[] { 0x00, 0x00 }, StreamCmd.Start, out _, out _));
    }

    [Fact]
    public void TryParseStreamResp_Stop_TwoBytes()
    {
        Assert.True(_codec.TryParseStreamResp(new byte[] { 0x00, 0x00 }, StreamCmd.Stop, out var status, out _));
        Assert.Equal(TuningStatus.Ok, status);
    }

    [Fact]
    public void TryParseStreamResp_OtaBusy_Surfaces()
    {
        // OTA 会话中 START 被拒：[01][03][div]
        Assert.True(_codec.TryParseStreamResp(new byte[] { 0x01, TuningStatus.OtaBusy, 0x01 }, StreamCmd.Start,
            out var status, out _));
        Assert.Equal(TuningStatus.OtaBusy, status);
    }

    // ================= 0x62 流帧解析（21B 大端 + 应答/流帧判别） =================

    [Fact]
    public void TryParseTelemetryFrame_GoldenVector_BigEndian()
    {
        // seq=0x1234, flags=0x05(冻结|零偏已标定), gx=-1, gy=2, gz=-32768, ax=16384, ay=-2, az=1,
        // rate_yaw=1234(12.34dps), rate_roll=-2000(-20.00dps), out_x=-3, out_y=7
        var payload = new byte[]
        {
            0x12, 0x34,             // seq
            0x05,                   // flags
            0xFF, 0xFF,             // gx = -1
            0x00, 0x02,             // gy = 2
            0x80, 0x00,             // gz = -32768
            0x40, 0x00,             // ax = 16384
            0xFF, 0xFE,             // ay = -2
            0x00, 0x01,             // az = 1
            0x04, 0xD2,             // rate_yaw = 1234 → 12.34dps
            0xF8, 0x30,             // rate_roll = -2000 → -20.00dps
            0xFD,                   // out_x = -3
            0x07,                   // out_y = 7
        };
        Assert.Equal(21, payload.Length);

        Assert.True(_codec.TryParseTelemetryFrame(payload, out var f));
        Assert.Equal(0x1234, f.Seq);
        Assert.Equal(0x05, f.Flags);
        Assert.True(f.IsFrozen);
        Assert.False(f.IsDragIntent);
        Assert.True(f.IsBiasCalibrated);
        Assert.Equal(-1, f.RawGx);
        Assert.Equal(2, f.RawGy);
        Assert.Equal(-32768, f.RawGz);
        Assert.Equal(16384, f.RawAx);
        Assert.Equal(-2, f.RawAy);
        Assert.Equal(1, f.RawAz);
        Assert.Equal(1234, f.RateYaw);
        Assert.Equal(-2000, f.RateRoll);
        Assert.Equal(-3, f.OutX);
        Assert.Equal(7, f.OutY);
    }

    [Fact]
    public void TryParseTelemetryFrame_WrongLength_Rejected()
    {
        // 应答载荷（≤3B）混入 / 断帧 → 一律拒绝
        Assert.False(_codec.TryParseTelemetryFrame(new byte[] { 0x01, 0x00, 0x02 }, out _));
        Assert.False(_codec.TryParseTelemetryFrame(new byte[20], out _));
        Assert.False(_codec.TryParseTelemetryFrame(new byte[22], out _));
    }

    [Fact]
    public void StreamCmd_ResponseDiscriminator_MatchesDoc()
    {
        // 判别口径（对齐卷 06 §1）：载荷 ≤3B=应答、=21B=流帧
        Assert.True(StreamCmd.IsResponse(2));
        Assert.True(StreamCmd.IsResponse(3));
        Assert.False(StreamCmd.IsResponse(21));
        Assert.Equal(21, StreamCmd.FramePayloadLength);
    }

    // ================= 录制器：丢流（seq 回绕）/ 帧率 / 丢流率 =================

    private static TelemetryFrame Frame(ushort seq) =>
        new(seq, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    [Fact]
    public void Recorder_SeqGap_CountsDropped()
    {
        var r = new TelemetryRecorder();
        r.Add(0, Frame(1));
        r.Add(10, Frame(2));
        r.Add(30, Frame(5));   // 跳 3/4 → 丢 2

        Assert.Equal(3, r.Count);
        Assert.Equal(2, r.Dropped);
        Assert.Equal(2.0 / 5, r.DroppedRatio, 6);
    }

    [Fact]
    public void Recorder_SeqWraparound_NotCountedAsDropped()
    {
        var r = new TelemetryRecorder();
        r.Add(0, Frame(65533));
        r.Add(10, Frame(65534));
        r.Add(20, Frame(65535));
        r.Add(30, Frame(0));    // 回绕 → delta=1，非丢
        r.Add(40, Frame(1));

        Assert.Equal(5, r.Count);
        Assert.Equal(0, r.Dropped);
    }

    [Fact]
    public void Recorder_Fps_FromTimestamps()
    {
        var r = new TelemetryRecorder();
        for (int i = 0; i < 101; i++)          // 100 个间隔 / 2000ms → 50fps
            r.Add(i * 20, Frame((ushort)i));

        Assert.Equal(50.0, r.Fps, 3);
    }

    [Fact]
    public void Recorder_EmptyAndSingle_Safe()
    {
        var r = new TelemetryRecorder();
        Assert.Equal(0, r.Fps);
        Assert.Equal(0, r.DroppedRatio);

        r.Add(5, Frame(0));
        Assert.Equal(0, r.Fps);                // 单帧无间隔 → 0
    }

    // ================= CSV（对齐卷 06 §2 列序 + meta 尾注） =================

    [Fact]
    public void TelemetryCsv_Header_And_Row_Golden()
    {
        Assert.Equal("pc_rx_ts,seq,flags,raw_gx,raw_gy,raw_gz,raw_ax,raw_ay,raw_az,rate_yaw,rate_roll,out_x,out_y",
            TelemetryCsv.Header);

        var f = new TelemetryFrame(0x1234, 5, -1, 2, -32768, 16384, -2, 1, 1234, -2000, -3, 7);
        Assert.Equal("1000,4660,5,-1,2,-32768,16384,-2,1,1234,-2000,-3,7",
            TelemetryCsv.Row(1000, f));
    }

    [Fact]
    public void TelemetryCsv_Meta_Golden()
    {
        Assert.Equal("# meta: fps=49.8,dropped=3,div=1,fw=0.12.119",
            TelemetryCsv.Meta(49.76, 3, 1, "0.12.119"));
        Assert.Equal("# meta: fps=0.0,dropped=0,div=2,fw=unknown",
            TelemetryCsv.Meta(0, 0, 2, null));
    }

    [Fact]
    public void TelemetryCsv_Build_FullFile()
    {
        var r = new TelemetryRecorder();
        r.Add(100, new TelemetryFrame(0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        r.Add(120, new TelemetryFrame(2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));   // 丢 seq=1

        var text = TelemetryCsv.Build(r.Samples, r.Fps, r.Dropped, 1, "0.12.119");
        var lines = text.Split('\n');

        Assert.StartsWith(TelemetryCsv.Header, lines[0]);
        Assert.Equal("100,0,0,1,0,0,0,0,0,0,0,0,0\r", lines[1]);   // AppendLine → \r\n
        Assert.Equal("120,2,0,0,0,0,0,0,0,0,0,0,0\r", lines[2]);
        Assert.StartsWith("# meta: fps=", lines[3]);
        Assert.Contains("dropped=1", lines[3]);
    }

    // ================= 定点单位口径（对齐卷 06 §1 笔端落码定案：raw 非原码） =================

    [Fact]
    public void Telemetry_FixedPoint_Units()
    {
        // 定案：陀螺 raw=0.01dps、加速度 raw=0.001g（app 层定点化+饱和，PC 直读物理量不换算——
        // 原"LSB 原码+FS 系数"提案作废，CompanyConfig 不落换算项）。
        // 帧内 rate 与 raw_gx 同单位（0.01dps）：1234 → 12.34dps；削顶=±32767 饱和（非回绕）。
        Assert.Equal(12.34f, 1234 * 0.01f, 4);
        Assert.Equal(0.001f, short.MaxValue * 0.001f / 32767, 6);   // accel 满幅 ≈32.767g（饱和）
    }
}
