// <copyright file="CompanyFrameTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Text;
using PencilBox.Core.Protocol.Company;

namespace PencilBox.Tests;

/// <summary>
/// 公司 V0.9 帧底座与命令面的**黄金向量**测试（离线，不依赖设备）。
///
/// 期望值一律取**骨架 §6.1 权威 `LEN` 表**（含 `0x51` 写请求 `0x2B`、`0x52` 请求 `0x04`、`0x52` 答 02 `0x2A`）；
/// **不引公司原件示例**（原件 `LEN` 有多处 +1 家族笔误，骨架 §6.2 P1/P2；联审 L-04）。
/// </summary>
public class CompanyFrameTests
{
    private static readonly byte[] Mac = { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };
    private const string BtName = "SunVoteR1:AABBCCDDEEFF";

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);
    private static byte[] Bytes(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    // ================= 组帧：请求 =================

    [Fact]
    public void Build_ReadVersion_Request_IsGoldenVector()
    {
        var frame = CompanyFrame.Build(CompanyFrame.BaseCmdRequest, CompanyCmd.ReadVersion, new byte[] { 0x00 });

        Assert.Equal("F5AAAA0461010300", Hex(frame));
        Assert.Equal(0x04, frame[3]);   // 权威 LEN（原件示例 0x1D 属笔误家族）
    }

    [Fact]
    public void Build_PairRead_Request_IsGoldenVector()
    {
        var frame = CompanyFrame.Build(CompanyFrame.BaseCmdRequest, CompanyCmd.PairInfo,
            new byte[] { CompanyParam.PairRead, 0x00 });

        Assert.Equal("F5AAAA056101510100", Hex(frame));
        Assert.Equal(0x05, frame[3]);
    }

    [Fact]
    public void Build_PairEnter_Request_IsGoldenVector()
    {
        var frame = CompanyFrame.Build(CompanyFrame.BaseCmdRequest, CompanyCmd.PairEnter,
            new byte[] { CompanyParam.PairEnter });

        // 权威：负载 `61 01 52 01` = 4B → LEN=0x04（总 8B）
        Assert.Equal("F5AAAA0461015201", Hex(frame));
        Assert.Equal(8, frame.Length);
    }

    [Fact]
    public void Build_PairWrite_Request_HasAuthoritativeLen()
    {
        var frame = CompanyFrame.Build(CompanyFrame.BaseCmdRequest, CompanyCmd.PairInfo, BuildPairWritePayload(0x00));

        Assert.Equal(47, frame.Length);            // 4 + LEN(43)
        Assert.Equal(0x2B, frame[3]);              // 权威 LEN=0x2B（43B）
        Assert.NotEqual(0x2C, frame[3]);           // 守护：原件 +1 笔误家族
        Assert.Equal(CompanyParam.PairWrite, frame[7]);
        Assert.Equal(0x00, frame[8]);              // NO=0
        Assert.Equal(Mac, frame[9..15]);
        Assert.Equal(BtName, ReadName(frame.AsSpan(15, CompanyCodec.BtNameLength)));
    }

    // ================= 解帧：应答 =================

    [Fact]
    public void TryParse_ReadVersion_Response_Golden()
    {
        var logical = Bytes("F5AAAA08E101030000010203");

        Assert.True(CompanyFrame.TryParse(logical, out var baseCmd, out var cmdType, out var data));
        Assert.Equal(CompanyFrame.BaseCmdResponse, baseCmd);
        Assert.Equal(CompanyCmd.ReadVersion, cmdType);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x01, 0x02, 0x03 }, data);

        var codec = new CompanyCodec();
        Assert.True(codec.TryParseVersionResp(data, out var info));
        Assert.Equal(CompanyStatus.Ok, info.Status);
        Assert.Equal(0x00, info.Model);
        Assert.Equal((byte)0x01, info.SverMajor);
        Assert.Equal((byte)0x02, info.SverMinor);
        Assert.Equal((byte)0x03, info.SverPatch);
    }

    [Fact]
    public void TryParse_PairRead_Response_Golden_HasLen0x2C()
    {
        var logical = BuildPairReadResp();

        Assert.Equal(48, logical.Length);          // 4 + LEN(44)
        Assert.Equal(0x2C, logical[3]);

        var codec = new CompanyCodec();
        Assert.True(CompanyFrame.TryParse(logical, out _, out var cmdType, out var data));
        Assert.Equal(CompanyCmd.PairInfo, cmdType);
        Assert.True(codec.TryParsePairReadResp(data, out var status, out var info));

        Assert.Equal(CompanyStatus.Ok, status);
        Assert.NotNull(info);
        Assert.Equal(0x00, info!.No);
        Assert.Equal(Mac, info.MacDisplay6);
        Assert.Equal(BtName, info.BtName);
    }

    [Fact]
    public void TryParse_PairEnter_Response02_Golden_HasLen0x2A()
    {
        var data = new List<byte> { PairEnterStatus.Success };
        data.AddRange(Mac);
        data.AddRange(Name32(BtName));

        var logical = CompanyFrame.Build(CompanyFrame.BaseCmdResponse, CompanyCmd.PairEnter, data.ToArray());

        Assert.Equal(46, logical.Length);          // 4 + LEN(42)
        Assert.Equal(0x2A, logical[3]);

        var codec = new CompanyCodec();
        Assert.True(CompanyFrame.TryParse(logical, out _, out _, out var payload));
        Assert.True(codec.TryParsePairEnterResp(payload, out var status, out var info));

        Assert.Equal(PairEnterStatus.Success, status);
        Assert.NotNull(info);
        Assert.Equal(Mac, info!.MacDisplay6);
        Assert.Equal(BtName, info.BtName);
    }

    [Fact]
    public void TryParse_PairEnter_Response01_HasNoAttachedInfo()
    {
        var codec = new CompanyCodec();

        Assert.True(codec.TryParsePairEnterResp(new byte[] { PairEnterStatus.Entered }, out var status, out var info));
        Assert.Equal(PairEnterStatus.Entered, status);
        Assert.Null(info);
    }

    [Fact]
    public void TryParsePairRead_StatusFail_IsStructurallyValid()
    {
        var codec = new CompanyCodec();
        var payload = new byte[] { CompanyParam.PairRead, CompanyStatus.Fail, 0x00, 0, 0, 0, 0, 0, 0 };

        Assert.True(codec.TryParsePairReadResp(payload, out var status, out var info));
        Assert.Equal(CompanyStatus.Fail, status);
        Assert.Null(info);
    }

    [Fact]
    public void TryParsePairWrite_Resp_ReturnsStatusAndNo()
    {
        var codec = new CompanyCodec();

        Assert.True(codec.TryParsePairWriteResp(
            new byte[] { CompanyParam.PairWrite, CompanyStatus.ParamError, 0x00 }, out var status, out var no));
        Assert.Equal(CompanyStatus.ParamError, status);   // 笔端不支持写时恒回 0x01
        Assert.Equal(0x00, no);

        Assert.False(codec.TryParsePairWriteResp(new byte[] { CompanyParam.PairWrite, 0x00 }, out _, out _));
    }

    [Theory]
    [InlineData("0011AA0461010300")]            // 非 F5 帧头
    [InlineData("F5AAAA0461020300")]            // BASE_ID ≠ 0x01（`61 02`）
    [InlineData("F5AAAA03610103")]              // LEN < 4
    [InlineData("F5AAAA08E1010300")]            // 帧未收满（LEN=8 但只有 8B）
    public void TryParse_RejectsIllegalFrame(string hex)
    {
        Assert.False(CompanyFrame.TryParse(Bytes(hex), out _, out _, out _));
    }

    // ================= USB 定长载体：按 LEN 去补零 =================

    [Fact]
    public void TryTrimToLogical_StripsUsbPadding()
    {
        var logical = CompanyFrame.Build(CompanyFrame.BaseCmdRequest, CompanyCmd.ReadVersion, new byte[] { 0x00 });
        var report = new byte[64];
        logical.CopyTo(report, 0);

        Assert.True(CompanyFrame.TryTrimToLogical(report, out var trimmed));
        Assert.Equal(logical, trimmed);
    }

    [Fact]
    public void TryTrimToLogical_RejectsShortOrGarbage()
    {
        Assert.False(CompanyFrame.TryTrimToLogical(new byte[64], out _));
        Assert.False(CompanyFrame.TryTrimToLogical(new byte[] { 0xF5, 0xAA }, out _));
    }

    // ================= 串口变长装配 =================

    [Fact]
    public void FrameReader_ReassemblesAcrossChunks()
    {
        var logical = BuildPairReadResp();
        var reader = new CompanyFrameReader();

        Assert.Empty(reader.Append(logical.AsSpan(0, 5)));       // 帧收了一半
        Assert.True(reader.PendingLength > 0);

        var frames = reader.Append(logical.AsSpan(5));
        Assert.Single(frames);
        Assert.Equal(logical, frames[0]);
        Assert.Equal(0, reader.PendingLength);
    }

    [Fact]
    public void FrameReader_EmitsTwoFramesFromOneChunk()
    {
        var a = CompanyFrame.Build(CompanyFrame.BaseCmdRequest, CompanyCmd.ReadVersion, new byte[] { 0x00 });
        var b = CompanyFrame.Build(CompanyFrame.BaseCmdRequest, CompanyCmd.PairEnter, new byte[] { CompanyParam.PairEnter });

        var frames = new CompanyFrameReader().Append(a.Concat(b).ToArray());

        Assert.Equal(2, frames.Count);
        Assert.Equal(a, frames[0]);
        Assert.Equal(b, frames[1]);
    }

    [Fact]
    public void FrameReader_ResyncsAfterGarbage()
    {
        var junk = new byte[] { 0x11, 0x22, 0x33, 0xF5, 0x00 };   // 含半个帧头
        var logical = CompanyFrame.Build(CompanyFrame.BaseCmdRequest, CompanyCmd.ReadVersion, new byte[] { 0x00 });

        var reader = new CompanyFrameReader();
        Assert.Empty(reader.Append(junk));

        var frames = reader.Append(logical);
        Assert.Single(frames);
        Assert.Equal(logical, frames[0]);
    }

    [Fact]
    public void FrameReader_SkipsFrameWithInvalidLen()
    {
        // LEN=0x02（非法）→ 丢该 F5 后重新同步，仍能取出紧跟其后的合法帧
        var bad = Bytes("F5AAAA026101");
        var good = CompanyFrame.Build(CompanyFrame.BaseCmdRequest, CompanyCmd.ReadVersion, new byte[] { 0x00 });

        var frames = new CompanyFrameReader().Append(bad.Concat(good).ToArray());

        Assert.Single(frames);
        Assert.Equal(good, frames[0]);
    }

    [Fact]
    public void FrameReader_ResetDropsHalfFrame()
    {
        var logical = BuildPairReadResp();
        var reader = new CompanyFrameReader();

        reader.Append(logical.AsSpan(0, 6));
        reader.Reset();

        Assert.Equal(0, reader.PendingLength);
        Assert.Single(reader.Append(logical));   // 重同步后完整帧仍可取出
    }

    // ================= 黄金应答/负载组装 =================

    /// <summary>`0x51` 读应答：`E1 01 51 01 STATUS(00) NO(00) MAC6 NAME32`（LEN=0x2C / 总 48B）</summary>
    private static byte[] BuildPairReadResp()
    {
        var data = new List<byte> { CompanyParam.PairRead, CompanyStatus.Ok, 0x00 };
        data.AddRange(Mac);
        data.AddRange(Name32(BtName));

        return CompanyFrame.Build(CompanyFrame.BaseCmdResponse, CompanyCmd.PairInfo, data.ToArray());
    }

    /// <summary>`0x51` 写请求负载：`PARAM(02) NO(1) MAC(6) NAME(32)` = 40B → LEN=0x2B</summary>
    private static byte[] BuildPairWritePayload(byte no)
    {
        var data = new List<byte> { CompanyParam.PairWrite, no };
        data.AddRange(Mac);
        data.AddRange(Name32(BtName));
        return data.ToArray();
    }

    /// <summary>定长 32B 的 `BT_NAME`（ASCII 截断 + 0 补齐）</summary>
    private static byte[] Name32(string name)
    {
        var buffer = new byte[CompanyCodec.BtNameLength];
        var bytes = Encoding.ASCII.GetBytes(name);
        Array.Copy(bytes, buffer, Math.Min(bytes.Length, buffer.Length));
        return buffer;
    }

    private static string ReadName(ReadOnlySpan<byte> raw)
    {
        int end = raw.IndexOf((byte)0x00);
        if (end < 0)
            end = raw.Length;
        return Encoding.ASCII.GetString(raw[..end]);
    }
}
