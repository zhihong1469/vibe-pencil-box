// <copyright file="AudioPassthroughTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Audio;

namespace PencilBox.Tests;

/// <summary>
/// 扩音直通纯逻辑单测：环形缓冲（丢最旧语义）+ 零前视格式转换（跨块一致性）
/// </summary>
public class AudioPassthroughTests
{
    // ===== PcmRingBuffer =====

    [Fact]
    public void RingBuffer_WriteReadRoundtrip()
    {
        var ring = new PcmRingBuffer(16);
        var data = Enumerable.Range(0, 10).Select(i => (byte)i).ToArray();

        ring.Write(data);
        var dest = new byte[10];
        var read = ring.Read(dest);

        Assert.Equal(10, read);
        Assert.Equal(data, dest);
        Assert.Equal(0, ring.DroppedBytes);
        Assert.Equal(10, ring.TotalWrittenBytes);
        Assert.Equal(10, ring.TotalReadBytes);
    }

    [Fact]
    public void RingBuffer_OverflowDropsOldest()
    {
        var ring = new PcmRingBuffer(8);
        ring.Write(new byte[] { 1, 2, 3, 4, 5, 6 });      // 6B
        ring.Write(new byte[] { 7, 8, 9, 10 });           // +4B → 溢出 2B，丢 1,2

        var dest = new byte[8];
        var read = ring.Read(dest);

        Assert.Equal(8, read);
        Assert.Equal(new byte[] { 3, 4, 5, 6, 7, 8, 9, 10 }, dest);
        Assert.Equal(2, ring.DroppedBytes);
    }

    [Fact]
    public void RingBuffer_WrapAround()
    {
        var ring = new PcmRingBuffer(4);
        var dest = new byte[4];

        ring.Write(new byte[] { 1, 2, 3 });
        Assert.Equal(3, ring.Read(dest));

        ring.Write(new byte[] { 4, 5, 6, 7 });            // 跨写指针回绕
        var read = ring.Read(dest.AsSpan(0, 4));
        Assert.Equal(4, read);
        Assert.Equal(new byte[] { 4, 5, 6, 7 }, dest[..4]);
    }

    [Fact]
    public void RingBuffer_ReadEmptyReturnsZero()
    {
        var ring = new PcmRingBuffer(8);
        Assert.Equal(0, ring.Read(new byte[8]));
    }

    // ===== SampleFormatConverter =====

    private static byte[] Pcm16FromDoubles(params double[] v) =>
        v.Select(x => BitConverter.GetBytes((short)Math.Clamp(x * 32767, short.MinValue, short.MaxValue)))
         .SelectMany(b => b).ToArray();

    private static float[] FloatsFromBytes(byte[] bytes) =>
        Enumerable.Range(0, bytes.Length / 4).Select(i => BitConverter.ToSingle(bytes, i * 4)).ToArray();

    [Fact]
    public void Converter_Pcm16PassthroughSameRate()
    {
        var c = new SampleFormatConverter(16000, 16, false, 1, 16000, 16, false, 1);
        Assert.True(c.IsPassthrough);

        var input = Pcm16FromDoubles(0.1, -0.2, 0.3, 0.5);
        var output = new byte[c.MaxOutputBytesFor(input.Length)];
        var written = c.Convert(input, output);

        Assert.Equal(input.Length, written);
        Assert.Equal(input, output[..written]);
    }

    [Fact]
    public void Converter_Upsample16kTo48k_PreservesDcLevel()
    {
        // 恒定输入 → 恒定输出（幅度保持），且输出长度 ≈ 3×
        var c = new SampleFormatConverter(16000, 16, false, 1, 48000, 32, true, 1);
        var input = Pcm16FromDoubles(Enumerable.Repeat(0.5, 160).ToArray());   // 10ms
        var output = new byte[c.MaxOutputBytesFor(input.Length)];
        var written = c.Convert(input, output);
        var floats = FloatsFromBytes(output[..written]);

        Assert.InRange(floats.Length, 48000 * 10 / 1000 - 8, 48000 * 10 / 1000 + 8);   // ≈480 样本（10ms @48k）
        Assert.All(floats, f => Assert.InRange(f, 0.49, 0.51));
    }

    [Fact]
    public void Converter_ChunkedFeed_MatchesSingleShot()
    {
        // 跨块状态一致性：整段一次喂 vs 帧对齐切成奇数帧小块喂，输出逐样本一致
        // （WASAPI 回调保证整帧边界，转换器按整帧消费输入）
        var c1 = new SampleFormatConverter(16000, 16, false, 1, 48000, 32, true, 2);
        var c2 = new SampleFormatConverter(16000, 16, false, 1, 48000, 32, true, 2);

        var signal = Enumerable.Range(0, 300).Select(i => Math.Sin(2 * Math.PI * 440 * i / 16000.0) * 0.8).ToArray();
        var input = Pcm16FromDoubles(signal);

        var out1 = new byte[c1.MaxOutputBytesFor(input.Length)];
        var w1 = c1.Convert(input, out1);

        var out2 = new List<byte>();
        for (var offset = 0; offset < input.Length; offset += 6)     // 每次 3 帧（6B），跨块续接
        {
            var chunk = input.AsSpan(offset, Math.Min(6, input.Length - offset));
            var buf = new byte[c2.MaxOutputBytesFor(chunk.Length)];
            var w = c2.Convert(chunk, buf);
            out2.AddRange(buf[..w]);
        }

        var a = FloatsFromBytes(out1[..w1]);
        var b = FloatsFromBytes(out2.ToArray());
        Assert.True(a.Length > 100);
        Assert.Equal(a.Length, b.Length);
        for (var i = 0; i < a.Length; i++)
            Assert.Equal(a[i], b[i], 6);
    }

    [Fact]
    public void Converter_MonoToStereoAndDownmix()
    {
        // 1ch → 2ch：两声道相同
        var up = new SampleFormatConverter(16000, 16, false, 1, 16000, 32, true, 2);
        var input = Pcm16FromDoubles(0.25, -0.25);
        var output = new byte[up.MaxOutputBytesFor(input.Length)];
        var w = up.Convert(input, output);
        var f = FloatsFromBytes(output[..w]);

        Assert.Equal(4, f.Length);   // 2 帧 × 2ch
        Assert.Equal(f[0], f[1], 6);
        Assert.Equal(f[2], f[3], 6);
        Assert.InRange(f[0], 0.24, 0.26);

        // 2ch → 1ch：均值下混
        var down = new SampleFormatConverter(16000, 16, false, 2, 16000, 16, false, 1);
        var in2 = Pcm16FromDoubles(0.5, 0.0, 0.5, 0.0);   // 2 帧：L=0.5, R=0
        var out2 = new byte[down.MaxOutputBytesFor(in2.Length)];
        var w2 = down.Convert(in2, out2);
        var s2 = ShortsFromBytes(out2[..w2]);
        Assert.Equal(2, s2.Length);
        Assert.All(s2, v => Assert.InRange(v, 8100, 8300));   // ≈0.25 * 32767
    }

    private static short[] ShortsFromBytes(byte[] bytes) =>
        Enumerable.Range(0, bytes.Length / 2).Select(i => BitConverter.ToInt16(bytes, i * 2)).ToArray();
}
