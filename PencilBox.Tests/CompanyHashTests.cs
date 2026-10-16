// <copyright file="CompanyHashTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Text;
using PencilBox.Core.Protocol.Company;
using PencilBox.Core.SerialScope;

namespace PencilBox.Tests;

/// <summary>
/// `0x04` 读 Git Hash 内部指令——上位机编解码与显示口径
///（协议定稿：`_share/issues/0-待处理/产品落地/版本管理/Box/读hash内部指令-0x04协议定稿与上位机对齐（最终版）.md`）：
/// - 请求 `61 01 04 target`（LEN=0x04），canonical：笔端串口轨 `0x11` / 基站 USB 轨 `0x00`（§3.2）；
/// - 应答 `E1 01 04 STATUS HASH40`（LEN=0x2C）：STATUS 1B + 40 字符 hex **ASCII**（非二进制，§3.4）；
/// - STATUS：`00` 成功 / `FF` hash 不可用（理论不发生，§3.5）；占位全零 = 40 个 `'0'`（§3.4）。
/// </summary>
public class CompanyHashTests
{
    /// <summary>定稿 §1 黄金示例：40 字符 hex ASCII</summary>
    private const string GoldenHash = "17040604c521501de92c3915814aef98d5d27bc0";

    private static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b);

    private static byte[] HashData(byte status, string hash, int extraTail = 0)
    {
        var data = new byte[1 + hash.Length + extraTail];
        data[0] = status;
        Encoding.ASCII.GetBytes(hash).CopyTo(data, 1);
        return data;
    }

    // ================= 请求黄金向量（定稿 §1） =================

    [Fact]
    public void EncodeReadHash_GoldenVectors()
    {
        var codec = new CompanyCodec();

        // 基站 USB 轨：61 01 04 00（USB 侧补零至 64B 由通道层负责）
        Assert.Equal("F5AAAA0461010400", Hex(codec.EncodeReadHash(0x00)));

        // 笔端串口轨 canonical：61 01 04 11（定稿 §3.2；串口变长不补零）
        Assert.Equal("F5AAAA0461010411", Hex(codec.EncodeReadHash(0x11)));
    }

    // ================= 应答解析 =================

    [Fact]
    public void TryParseHashResp_GoldenAscii()
    {
        var codec = new CompanyCodec();

        var ok = codec.TryParseHashResp(HashData(0x00, GoldenHash), out var status, out var hash);

        Assert.True(ok);
        Assert.Equal(0x00, status);
        Assert.Equal(GoldenHash, hash);
    }

    [Fact]
    public void TryParseHashResp_StatusUnavailable_StillStructured()
    {
        var codec = new CompanyCodec();

        // STATUS=0xFF（hash 不可用，§3.5）——结构合法、业务失败，HASH 字段照收（由调用方决定提示）
        var ok = codec.TryParseHashResp(HashData(0xFF, GoldenHash), out var status, out var hash);

        Assert.True(ok);
        Assert.Equal(0xFF, status);
        Assert.Equal(GoldenHash, hash);
    }

    [Fact]
    public void TryParseHashResp_AllZeroPlaceholder_IsValid()
    {
        var codec = new CompanyCodec();

        // 宏缺席占位全零（40 个 '0'，§3.4）——合法应答，展示层提示占位
        var ok = codec.TryParseHashResp(HashData(0x00, new string('0', 40)), out var status, out var hash);

        Assert.True(ok);
        Assert.Equal(0x00, status);
        Assert.Equal(new string('0', 40), hash);
        Assert.All(hash.ToCharArray(), c => Assert.Equal('0', c));
    }

    [Theory]
    [InlineData(39)]   // HASH 差 1 字符（LEN=0x2B）
    [InlineData(0)]    // 仅 STATUS：缺 HASH
    public void TryParseHashResp_TooShort_IsIllegal(int hashLen)
    {
        var codec = new CompanyCodec();

        Assert.False(codec.TryParseHashResp(HashData(0x00, new string('a', hashLen)), out _, out _));
    }

    [Fact]
    public void TryParseHashResp_TrailingBytesIgnored_ForwardCompat()
    {
        var codec = new CompanyCodec();

        // 尾部多余字节忽略（前向兼容，同 0x03 的 24B RESERVED 口径）
        var ok = codec.TryParseHashResp(HashData(0x00, GoldenHash, extraTail: 4), out var status, out var hash);

        Assert.True(ok);
        Assert.Equal(0x00, status);
        Assert.Equal(GoldenHash, hash);
    }

    // ================= 串口示波器显示（同源解析，不另写一套） =================

    [Fact]
    public void Decoder_Hash_Request_ShowsTarget()
    {
        var decoder = new CompanyFrameDecoder();

        Assert.Equal("↓0x04 读 Git Hash（target=0x00）",
            decoder.Decode(CompanyFrame.Build(CompanyFrame.BaseCmdRequest, CompanyCmd.ReadHash, new byte[] { 0x00 })));
        Assert.Equal("↓0x04 读 Git Hash（target=0x11）",
            decoder.Decode(CompanyFrame.Build(CompanyFrame.BaseCmdRequest, CompanyCmd.ReadHash, new byte[] { 0x11 })));
    }

    [Fact]
    public void Decoder_Hash_Response_ShowsAsciiHash()
    {
        var decoder = new CompanyFrameDecoder();
        var frame = CompanyFrame.Build(CompanyFrame.BaseCmdResponse, CompanyCmd.ReadHash,
            HashData(0x00, GoldenHash));

        Assert.Equal($"↑0x04 应答 STA=00 HASH={GoldenHash}", decoder.Decode(frame));
    }

    [Fact]
    public void Decoder_Hash_Response_Unavailable()
    {
        var decoder = new CompanyFrameDecoder();
        var frame = CompanyFrame.Build(CompanyFrame.BaseCmdResponse, CompanyCmd.ReadHash,
            HashData(0xFF, GoldenHash));

        Assert.Equal("↑0x04 应答 STA=FF（hash 不可用）", decoder.Decode(frame));
    }
}
