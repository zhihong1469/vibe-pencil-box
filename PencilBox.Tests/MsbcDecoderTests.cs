// <copyright file="MsbcDecoderTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Ble.SoftReceiver;

namespace PencilBox.Tests;

/// <summary>
/// mSBC 解码器单测。金标准 = libsbc 2.0 sbc_decode 逐字节输出（向量见
/// <see cref="MsbcGoldenVectors"/>，生成方式 _scratch/msbc-ref/）——
/// 解码正确性不靠肉眼听感，靠与 C 参考实现逐位对拍。
/// </summary>
public class MsbcDecoderTests
{
    [Fact]
    public void Decode_GoldenFrames_BitExactAgainstLibsbc()
    {
        var decoder = new MsbcDecoder();
        var pcm = new byte[MsbcDecoder.PcmBytesPerFrame];

        // 三帧经同一实例顺序解码（V 状态跨帧连续，与金标准生成方式一致）
        Assert.Equal(MsbcDecodeStatus.Ok, decoder.DecodeWrapped(MsbcGoldenVectors.Frame1, pcm, out _));
        Assert.Equal(MsbcGoldenVectors.ExpectedPcm1, pcm);

        Assert.Equal(MsbcDecodeStatus.Ok, decoder.DecodeWrapped(MsbcGoldenVectors.Frame2, pcm, out _));
        Assert.Equal(MsbcGoldenVectors.ExpectedPcm2, pcm);

        Assert.Equal(MsbcDecodeStatus.Ok, decoder.DecodeWrapped(MsbcGoldenVectors.Frame3, pcm, out _));
        Assert.Equal(MsbcGoldenVectors.ExpectedPcm3, pcm);

        Assert.Equal(3, decoder.FramesDecoded);
        Assert.Equal(0, decoder.FramesCrcError);
        Assert.Equal(0, decoder.FramesSyncError);
    }

    [Fact]
    public void Decode_Reset_RestoresInitialState()
    {
        var decoder = new MsbcDecoder();
        var first = new byte[MsbcDecoder.PcmBytesPerFrame];
        var again = new byte[MsbcDecoder.PcmBytesPerFrame];

        Assert.Equal(MsbcDecodeStatus.Ok, decoder.DecodeWrapped(MsbcGoldenVectors.Frame1, first, out _));
        decoder.Reset();
        Assert.Equal(MsbcDecodeStatus.Ok, decoder.DecodeWrapped(MsbcGoldenVectors.Frame1, again, out _));

        Assert.Equal(first, again);
    }

    [Fact]
    public void DecodeWrapped_BadLength_ReturnsBadLength()
    {
        var decoder = new MsbcDecoder();
        var pcm = new byte[MsbcDecoder.PcmBytesPerFrame];

        Assert.Equal(MsbcDecodeStatus.BadLength,
            decoder.DecodeWrapped(new byte[MsbcDecoder.WrappedLength - 1], pcm, out _));
        Assert.Equal(MsbcDecodeStatus.BadLength,
            decoder.DecodeWrapped(new byte[MsbcDecoder.WrappedLength + 1], pcm, out _));
    }

    [Fact]
    public void DecodeWrapped_BadWrapperMarks_ReturnsBadWrapperMark()
    {
        var decoder = new MsbcDecoder();
        var pcm = new byte[MsbcDecoder.PcmBytesPerFrame];

        var badHead = (byte[])MsbcGoldenVectors.Frame1.Clone();
        badHead[0] = 0x02;
        Assert.Equal(MsbcDecodeStatus.BadWrapperMark, decoder.DecodeWrapped(badHead, pcm, out _));

        var badTail = (byte[])MsbcGoldenVectors.Frame1.Clone();
        badTail[^1] = 0x01;
        Assert.Equal(MsbcDecodeStatus.BadWrapperMark, decoder.DecodeWrapped(badTail, pcm, out _));
    }

    [Fact]
    public void Decode_SyncwordCorrupted_ReturnsBadSyncword()
    {
        var decoder = new MsbcDecoder();
        var pcm = new byte[MsbcDecoder.PcmBytesPerFrame];

        var bad = (byte[])MsbcGoldenVectors.Frame1.Clone();
        bad[2] = 0xAC; // 同步字 0xAD → 0xAC
        Assert.Equal(MsbcDecodeStatus.BadSyncword, decoder.DecodeWrapped(bad, pcm, out _));

        var badReserved = (byte[])MsbcGoldenVectors.Frame1.Clone();
        badReserved[3] = 0x01; // 保留字节 data[1]（57B 裸帧 [1]）≠0
        Assert.Equal(MsbcDecodeStatus.BadSyncword, decoder.DecodeWrapped(badReserved, pcm, out _));

        Assert.Equal(2, decoder.FramesSyncError);
    }

    [Fact]
    public void Decode_ScaleFactorCorrupted_FailsCrc()
    {
        var decoder = new MsbcDecoder();
        var pcm = new byte[MsbcDecoder.PcmBytesPerFrame];

        // 破坏尺度因子区（60B 帧 offset 7 = 57B 裸帧 [4] 首个尺度因子字节），CRC 必挂
        var bad = (byte[])MsbcGoldenVectors.Frame1.Clone();
        bad[7] ^= 0x10;
        Assert.Equal(MsbcDecodeStatus.CrcMismatch, decoder.DecodeWrapped(bad, pcm, out _));
        Assert.Equal(1, decoder.FramesCrcError);
        Assert.Equal(0, decoder.FramesDecoded);

        // CRC 失败不解码、不污染滤波器状态：修好同一帧照常过
        Assert.Equal(MsbcDecodeStatus.Ok, decoder.DecodeWrapped(MsbcGoldenVectors.Frame1, pcm, out _));
        Assert.Equal(1, decoder.FramesDecoded);
    }

    [Fact]
    public void Decode_SeqMarker_PassedThrough()
    {
        var decoder = new MsbcDecoder();
        var pcm = new byte[MsbcDecoder.PcmBytesPerFrame];

        Assert.Equal(MsbcDecodeStatus.Ok, decoder.DecodeWrapped(MsbcGoldenVectors.Frame1, pcm, out var m1));
        Assert.Equal(MsbcDecodeStatus.Ok, decoder.DecodeWrapped(MsbcGoldenVectors.Frame2, pcm, out var m2));
        Assert.Equal(0x08, m1);
        Assert.Equal(0x38, m2);
    }

    [Fact]
    public void Decode_PcmBufferTooSmall_Throws()
    {
        var decoder = new MsbcDecoder();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            decoder.DecodeWrapped(MsbcGoldenVectors.Frame1, new byte[239], out _));
    }
}
