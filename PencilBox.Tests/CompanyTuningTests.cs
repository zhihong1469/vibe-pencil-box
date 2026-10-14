// <copyright file="CompanyTuningTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Protocol.Company;

namespace PencilBox.Tests;

/// <summary>
/// `0x61` 内部调参（issues-pencil/01）codec 单测 + 工具端范围表对拍。
///
/// 黄金向量口径：帧 profile 取 <see cref="CompanyFrame"/>（正文 §5"帧内无 CRC"、§6.1 LEN 权威表）——
/// 01 卷 §7 示例向量尾缀 "+ CRC16" 为**勘误**（与正文 §5/§6.1 矛盾，落地按无 CRC 实现，回填时同步）。
/// VALUE4 = IEEE754 float32 **大端**：0.27f=3E 8A 3D 71 / 0.3f=3E 99 99 9A / 8.0f=41 00 00 00 /
/// 0.5f=3F 00 00 00 / 250f=43 7A 00 00。
/// </summary>
public class CompanyTuningTests
{
    private readonly CompanyCodec _codec = new();

    // ================= 0x61 编码（黄金向量） =================

    [Fact]
    public void EncodeTuningWrite_GoldenVector_Id1_0_27f()
    {
        // 01 卷 §7：写全局灵敏度 0.27 —— `F5 AA AA 09 61 01 61 02 01 3E 8A 3D 71`（LEN=0x09，无 CRC）
        var frame = _codec.EncodeTuningWrite(0x01, 0.27f);
        Assert.Equal(13, frame.Length);
        Assert.Equal(
            new byte[] { 0xF5, 0xAA, 0xAA, 0x09, 0x61, 0x01, 0x61, 0x02, 0x01, 0x3E, 0x8A, 0x3D, 0x71 },
            frame);
    }

    [Fact]
    public void EncodeTuningRead_Single_And_All_GoldenVectors()
    {
        // 单参读 id=2：`F5 AA AA 05 61 01 61 01 02`（LEN=0x05）
        Assert.Equal(
            new byte[] { 0xF5, 0xAA, 0xAA, 0x05, 0x61, 0x01, 0x61, 0x01, 0x02 },
            _codec.EncodeTuningRead(0x02));

        // 全表读 PARAM_ID=0xFF：`F5 AA AA 05 61 01 61 01 FF`
        Assert.Equal(
            new byte[] { 0xF5, 0xAA, 0xAA, 0x05, 0x61, 0x01, 0x61, 0x01, 0xFF },
            _codec.EncodeTuningRead(TuningParamMode.ParamIdAll));
    }

    [Fact]
    public void EncodeTuningWrite_Float32BigEndian_KnownConstants()
    {
        // VALUE4 大端：0.3f=3E 99 99 9A / 8.0f=41 00 00 00 / 0.5f=3F 00 00 00 / 250f=43 7A 00 00
        Assert.Equal(new byte[] { 0x3E, 0x99, 0x99, 0x9A }, Value4(_codec.EncodeTuningWrite(0x01, 0.3f)));
        Assert.Equal(new byte[] { 0x41, 0x00, 0x00, 0x00 }, Value4(_codec.EncodeTuningWrite(0x03, 8.0f)));
        Assert.Equal(new byte[] { 0x3F, 0x00, 0x00, 0x00 }, Value4(_codec.EncodeTuningWrite(0x05, 0.5f)));
        Assert.Equal(new byte[] { 0x43, 0x7A, 0x00, 0x00 }, Value4(_codec.EncodeTuningWrite(0x06, 250f)));
    }

    // ================= 0x61 解析：写应答（生效值 / 越界回旧值） =================

    [Fact]
    public void TryParseTuningWriteResp_Ok_ReturnsEffectiveValue()
    {
        var ok = _codec.TryParseTuningWriteResp(
            new byte[] { 0x02, 0x00, 0x01, 0x3E, 0x8A, 0x3D, 0x71 },
            out var status, out var paramId, out var effective);

        Assert.True(ok);
        Assert.Equal(TuningStatus.Ok, status);
        Assert.Equal(0x01, paramId);
        Assert.Equal(0.27f, effective, 5);
    }

    [Fact]
    public void TryParseTuningWriteResp_OutOfRange_ReturnsOldValue()
    {
        // STATUS=02：笔端不夹取，VALUE4 = 写前旧值（此处旧值 0.27）
        var ok = _codec.TryParseTuningWriteResp(
            new byte[] { 0x02, 0x02, 0x01, 0x3E, 0x8A, 0x3D, 0x71 },
            out var status, out _, out var effective);

        Assert.True(ok);
        Assert.Equal(TuningStatus.OutOfRange, status);
        Assert.Equal(0.27f, effective, 5);
    }

    // ================= 0x61 解析：单参读应答 =================

    [Fact]
    public void TryParseTuningReadResp_Ok_DecodesValue()
    {
        var ok = _codec.TryParseTuningReadResp(
            new byte[] { 0x01, 0x00, 0x05, 0x3F, 0x00, 0x00, 0x00 },
            out var status, out var paramId, out var value);

        Assert.True(ok);
        Assert.Equal(TuningStatus.Ok, status);
        Assert.Equal(0x05, paramId);
        Assert.Equal(0.5f, value, 5);
    }

    [Fact]
    public void TryParseTuningReadResp_UnknownId_StructureStillValid()
    {
        // STATUS=01 是业务失败、结构合法：归因权在调用方
        var ok = _codec.TryParseTuningReadResp(
            new byte[] { 0x01, 0x01, 0x7F, 0x00, 0x00, 0x00, 0x00 },
            out var status, out _, out _);

        Assert.True(ok);
        Assert.Equal(TuningStatus.UnknownId, status);
    }

    // ================= 0x61 解析：全表读应答（LEN=0x07+5N） =================

    [Fact]
    public void TryParseTuningReadAllResp_SixParams_GoldenLen25()
    {
        // 6 参全表：data = 01 00 FF 06 + N×5（34B）→ 整帧 LEN = 3 + 34 = 0x25
        var data = new List<byte> { 0x01, 0x00, 0xFF, 0x06 };
        data.AddRange(new byte[] { 0x01, 0x3E, 0x99, 0x99, 0x9A });   // 0.3
        data.AddRange(new byte[] { 0x02, 0x3E, 0x8A, 0x3D, 0x71 });   // 0.27
        data.AddRange(new byte[] { 0x03, 0x41, 0x00, 0x00, 0x00 });   // 8.0
        data.AddRange(new byte[] { 0x04, 0x41, 0x00, 0x00, 0x00 });   // 8.0
        data.AddRange(new byte[] { 0x05, 0x3F, 0x00, 0x00, 0x00 });   // 0.5
        data.AddRange(new byte[] { 0x06, 0x43, 0x7A, 0x00, 0x00 });   // 250

        var ok = _codec.TryParseTuningReadAllResp(data.ToArray(), out var status, out var items);

        Assert.True(ok);
        Assert.Equal(TuningStatus.Ok, status);
        Assert.Equal(6, items.Count);
        Assert.Equal(0.3f, items[0].Value, 4);
        Assert.Equal(0.27f, items[1].Value, 5);
        Assert.Equal(8.0f, items[2].Value, 5);
        Assert.Equal(0.5f, items[4].Value, 5);
        Assert.Equal(250f, items[5].Value, 5);

        // 整帧 LEN=0x25（01 卷 §7 全表应答帧长对拍；无 CRC）
        var frame = CompanyFrame.Build(CompanyFrame.BaseCmdResponse, CompanyCmd.TuningParam, data.ToArray());
        Assert.Equal(0x25, frame[3]);
        Assert.Equal(41, frame.Length);
    }

    [Fact]
    public void TryParseTuningReadAllResp_CountPayloadMismatch_Fails()
    {
        // COUNT=2 但只带 1×5 载荷：结构损坏，拒绝
        var data = new byte[] { 0x01, 0x00, 0xFF, 0x02, 0x01, 0x3E, 0x99, 0x99, 0x9A };
        Assert.False(_codec.TryParseTuningReadAllResp(data, out _, out _));
    }

    [Fact]
    public void TryParseTuningResp_WrongModeByte_Fails()
    {
        // 读应答首字节必须 01 / 写应答首字节必须 02（防串型）
        Assert.False(_codec.TryParseTuningReadResp(new byte[] { 0x02, 0x00, 0x01, 0, 0, 0, 0 }, out _, out _, out _));
        Assert.False(_codec.TryParseTuningWriteResp(new byte[] { 0x01, 0x00, 0x01, 0, 0, 0, 0 }, out _, out _, out _));
        // 全表通配位必须是 FF
        Assert.False(_codec.TryParseTuningReadAllResp(new byte[] { 0x01, 0x00, 0x00, 0x00 }, out _, out _));
        // 截短
        Assert.False(_codec.TryParseTuningWriteResp(new byte[] { 0x02, 0x00, 0x01, 0x3E, 0x8A }, out _, out _, out _));
    }

    // ================= 工具端范围表（02 卷 §3：与 01 卷 §5 单一来源对拍） =================

    [Fact]
    public void TuningParamTable_SixEntries_MatchesProtocolSpec()
    {
        Assert.Equal(6, TuningParamTable.All.Count);

        var expected = new (byte Id, float Factory, float Min, float Max)[]
        {
            (0x01, 0.3f, 0.05f, 2.00f),
            (0x02, 0.27f, 0.05f, 1.00f),
            (0x03, 8.0f, 1.0f, 50.0f),
            (0x04, 8.0f, 1.0f, 50.0f),
            (0x05, 0.5f, 0.0f, 5.0f),
            (0x06, 250f, 0.0f, 1000f),
        };

        for (int i = 0; i < expected.Length; i++)
        {
            var d = TuningParamTable.All[i];
            Assert.Equal(expected[i].Id, d.Id);
            Assert.Equal(expected[i].Factory, d.Factory, 5);
            Assert.Equal(expected[i].Min, d.Min, 5);
            Assert.Equal(expected[i].Max, d.Max, 5);
        }

        Assert.NotNull(TuningParamTable.Find(0x06));
        Assert.Null(TuningParamTable.Find(0x07));
        Assert.Contains("0x01", TuningParamTable.IdListText);
    }

    /// <summary>取写帧的 VALUE4 段（帧尾 4B）</summary>
    private static byte[] Value4(byte[] frame) => frame[^4..];
}
