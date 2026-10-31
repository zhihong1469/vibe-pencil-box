// <copyright file="MsbcDecoder.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Ble.SoftReceiver;

/// <summary>mSBC 帧解码结果。</summary>
public enum MsbcDecodeStatus
{
    Ok = 0,

    /// <summary>输入长度不足（裸帧 ≠57B / 包装帧 ≠60B）</summary>
    BadLength,

    /// <summary>包装帧头/尾标记错（data[0]≠0x01 或 data[59]≠0x00）</summary>
    BadWrapperMark,

    /// <summary>同步字错（payload[0]≠0xAD）或保留字节非 0</summary>
    BadSyncword,

    /// <summary>CRC-8 校验失败（覆盖 byte1+byte2+8 个尺度因子，48bit）</summary>
    CrcMismatch,
}

/// <summary>
/// mSBC 解码器（纯 C#，单声道 16kHz；60B 包装帧 → 240B PCM s16LE）。
///
/// 算法出处：Bluetooth A2DP 规范附录 B 的 SBC 定点解码算法，逐操作对齐
/// linux-bluez libsbc 2.0（<c>sbc.c</c>：sbc_crc8 / sbc_calculate_bits_internal /
/// sbc_unpack_frame_internal / sbc_synthesize_eight），mSBC = 固定参数 SBC
/// （16kHz 单声道、15 blocks、8 subbands、bitpool 26、LOUDNESS）。
/// 包装帧 = [0x01][seq 标记][57B 裸帧][0x00]（基站 app_dongle.c 串口转发注释 +
/// 笔端真机帧实证；seq 标记实测循环 0x08/0x38/0xC8/0xF8，解析只透传不校验）。
///
/// 状态连续性：合成滤波器 V/offset 跨帧保持（与 libsbc <c>sbc_t</c> 实例同语义），
/// 断连重开请调 <see cref="Reset"/>，否则解码出历史尾音混叠。
/// 乘加链全部用 int32 回绕算术（unchecked），与 C 参考实现逐位一致——
/// 金标准单测用 libsbc sbc_decode 产物逐字节对拍。
/// </summary>
public sealed class MsbcDecoder
{
    /// <summary>裸 mSBC 帧长（57B）</summary>
    public const int PayloadLength = VpProtocol.MsbcPayloadLen;

    /// <summary>笔端包装帧长（60B = [0x01][seq][57B][0x00]）</summary>
    public const int WrappedLength = VpProtocol.MsbcFrameLen;

    /// <summary>每帧输出 PCM 字节数（120 样点 × 2B，s16LE 单声道 16kHz = 7.5ms）</summary>
    public const int PcmBytesPerFrame = 240;

    /// <summary>mSBC 同步字（payload[0]）</summary>
    private const byte Syncword = 0xAD;

    /// <summary>固定块数（mSBC = 15）</summary>
    private const int Blocks = 15;

    private const int Subbands = 8;
    private const int Bitpool = 26;
    private const int FixedExtraBits = 2; // SBCDEC_FIXED_EXTRA_BITS
    private const int StagedShift = 15;   // SCALE8_STAGED1_BITS

    // ---- 合成滤波器状态（跨帧保持；单声道只需 1 通道） ----
    private readonly int[] _v = new int[170];
    private readonly int[] _offset = new int[16];

    private readonly int[] _scaleFactor = new int[Subbands];
    private readonly int[] _bits = new int[Subbands];
    private readonly int[] _levels = new int[Subbands];
    private readonly int[] _sbSample = new int[Subbands]; // 当前 block 的 8 子带样点

    /// <summary>累计成功解码帧数</summary>
    public long FramesDecoded { get; private set; }

    /// <summary>累计 CRC 校验失败帧数</summary>
    public long FramesCrcError { get; private set; }

    /// <summary>累计同步字/保留字节错误帧数</summary>
    public long FramesSyncError { get; private set; }

    public MsbcDecoder()
    {
        Reset();
    }

    /// <summary>复位滤波器状态与计数器（断连重连时调用）。</summary>
    public void Reset()
    {
        Array.Clear(_v);
        for (int i = 0; i < 16; i++)
            _offset[i] = 10 * i + 10; // sbc_decoder_init
        FramesDecoded = 0;
        FramesCrcError = 0;
        FramesSyncError = 0;
    }

    /// <summary>解码笔端 60B 包装帧（[0x01][seq][57B][0x00]）。</summary>
    /// <param name="frame60">60B 包装帧</param>
    /// <param name="pcmOut">输出 ≥240B，成功时写入 240B PCM s16LE</param>
    /// <param name="seqMarker">包装帧 seq 标记字节（data[1]，透传诊断用）</param>
    public MsbcDecodeStatus DecodeWrapped(ReadOnlySpan<byte> frame60, Span<byte> pcmOut, out byte seqMarker)
    {
        if (pcmOut.Length < PcmBytesPerFrame)
            throw new ArgumentOutOfRangeException(nameof(pcmOut), $"需 ≥{PcmBytesPerFrame}B");
        seqMarker = 0;
        if (frame60.Length != WrappedLength)
            return MsbcDecodeStatus.BadLength;
        if (frame60[0] != VpProtocol.MsbcHeadMark || frame60[^1] != VpProtocol.MsbcTailMark)
            return MsbcDecodeStatus.BadWrapperMark;
        seqMarker = frame60[1];
        return Decode(frame60.Slice(2, PayloadLength), pcmOut);
    }

    /// <summary>解码裸 57B mSBC 帧 → 240B PCM（s16LE 单声道 16kHz）。</summary>
    public MsbcDecodeStatus Decode(ReadOnlySpan<byte> payload, Span<byte> pcmOut)
    {
        if (pcmOut.Length < PcmBytesPerFrame)
            throw new ArgumentOutOfRangeException(nameof(pcmOut), $"需 ≥{PcmBytesPerFrame}B");
        if (payload.Length != PayloadLength)
            return MsbcDecodeStatus.BadLength;
        if (payload[0] != Syncword || payload[1] != 0 || payload[2] != 0)
        {
            FramesSyncError++;
            return MsbcDecodeStatus.BadSyncword;
        }

        // ---- CRC-8：覆盖 byte1、byte2 与 8 个尺度因子（共 48bit） ----
        // crc_header 6B = [data[1], data[2], 尺度因子 4B]（对齐 sbc_unpack_frame_internal）
        Span<byte> crcHeader = stackalloc byte[6];
        crcHeader[0] = payload[1];
        crcHeader[1] = payload[2];
        ReadScaleFactors(payload, crcHeader);
        if (payload[3] != Crc8(crcHeader))
        {
            FramesCrcError++;
            return MsbcDecodeStatus.CrcMismatch;
        }

        CalculateBits();
        for (int sb = 0; sb < Subbands; sb++)
            _levels[sb] = (1 << _bits[sb]) - 1;

        // ---- 反量化 + 合成：15 blocks 逐块进行（对齐 sbc_unpack 的 blk 循环 +
        // sbc_synthesize_audio 的逐块合成，混合遍历等价：块间无耦合，仅 V 状态延续） ----
        int consumed = 64; // 32B 车头+CRC + 32bit 尺度因子（bit 位）
        int sampleIdx = 0;
        for (int blk = 0; blk < Blocks; blk++)
        {
            for (int sb = 0; sb < Subbands; sb++)
            {
                if (_levels[sb] == 0)
                {
                    _sbSample[sb] = 0;
                    continue;
                }
                int shift = _scaleFactor[sb] + 1 + FixedExtraBits;
                int audioSample = 0;
                for (int bit = 0; bit < _bits[sb]; bit++)
                {
                    if (((payload[consumed >> 3] >> (7 - (consumed & 7))) & 0x01) != 0)
                        audioSample |= 1 << (_bits[sb] - bit - 1);
                    consumed++;
                }
                // C: (int32)(((((uint64_t)a << 1) | 1) << shift) / levels) - (1 << shift)
                _sbSample[sb] = (int)((((long)audioSample << 1 | 1) << shift) / _levels[sb]) - (1 << shift);
            }

            SynthesizeEight(pcmOut, ref sampleIdx);
        }

        FramesDecoded++;
        return MsbcDecodeStatus.Ok;
    }

    /// <summary>读 8 个尺度因子（byte4..7 高低 nibble 交替），同时填 CRC 头部 byte2..5。</summary>
    private void ReadScaleFactors(ReadOnlySpan<byte> payload, Span<byte> crcHeader)
    {
        int consumed = 32;
        for (int sb = 0; sb < Subbands; sb++)
        {
            int sf = (payload[consumed >> 3] >> (4 - (consumed & 7))) & 0x0F;
            _scaleFactor[sb] = sf;
            crcHeader[2 + (sb >> 1)] |= (byte)(sf << (4 - ((sb & 1) * 4)));
            consumed += 4;
        }
    }

    /// <summary>
    /// 比特分配（LOUDNESS，16kHz 单声道 8 子带，bitpool 26）——
    /// 对齐 sbc_calculate_bits_internal 的 MONO 分支（A2DP 规范算法）。
    /// </summary>
    private void CalculateBits()
    {
        const int freqRow = 0; // SBC_FREQ_16000 = 0
        Span<int> bitneed = stackalloc int[Subbands];
        int maxBitneed = 0;

        for (int sb = 0; sb < Subbands; sb++)
        {
            if (_scaleFactor[sb] == 0)
            {
                bitneed[sb] = -5;
            }
            else
            {
                int loudness = _scaleFactor[sb] - SbcOffset8[freqRow][sb];
                bitneed[sb] = loudness > 0 ? loudness / 2 : loudness;
            }
            if (bitneed[sb] > maxBitneed)
                maxBitneed = bitneed[sb];
        }

        int bitcount = 0;
        int slicecount = 0;
        int bitslice = maxBitneed + 1;
        do
        {
            bitslice--;
            bitcount += slicecount;
            slicecount = 0;
            for (int sb = 0; sb < Subbands; sb++)
            {
                if (bitneed[sb] > bitslice + 1 && bitneed[sb] < bitslice + 16)
                    slicecount++;
                else if (bitneed[sb] == bitslice + 1)
                    slicecount += 2;
            }
        }
        while (bitcount + slicecount < Bitpool);

        if (bitcount + slicecount == Bitpool)
        {
            bitcount += slicecount;
            bitslice--;
        }

        for (int sb = 0; sb < Subbands; sb++)
        {
            if (bitneed[sb] < bitslice + 2)
                _bits[sb] = 0;
            else
            {
                _bits[sb] = bitneed[sb] - bitslice;
                if (_bits[sb] > 16)
                    _bits[sb] = 16;
            }
        }

        for (int sb = 0; bitcount < Bitpool && sb < Subbands; sb++)
        {
            if (_bits[sb] >= 2 && _bits[sb] < 16)
            {
                _bits[sb]++;
                bitcount++;
            }
            else if (bitneed[sb] == bitslice + 1 && Bitpool > bitcount + 1)
            {
                _bits[sb] = 2;
                bitcount += 2;
            }
        }

        for (int sb = 0; bitcount < Bitpool && sb < Subbands; sb++)
        {
            if (_bits[sb] < 16)
            {
                _bits[sb]++;
                bitcount++;
            }
        }
    }

    /// <summary>
    /// 8 子带合成滤波（对齐 sbc_synthesize_eight，单声道）：
    /// 16 相位移位 + 矩阵分布进 V，再按原型滤波器两相（m0/m1）抽 8 样点。
    /// 全程 int32 回绕算术，与 C 参考一致。
    /// </summary>
    private void SynthesizeEight(Span<byte> pcmOut, ref int sampleIdx)
    {
        // Shifting + distribute（V[170]，回卷时把前 9 个值复制到 160..168）
        for (int i = 0; i < 16; i++)
        {
            _offset[i]--;
            if (_offset[i] < 0)
            {
                _offset[i] = 159;
                for (int j = 0; j < 9; j++)
                    _v[j + 160] = _v[j];
            }

            int acc = SynMatrix8[i][7] * _sbSample[7];
            acc = SynMatrix8[i][6] * _sbSample[6] + acc;
            acc = SynMatrix8[i][5] * _sbSample[5] + acc;
            acc = SynMatrix8[i][4] * _sbSample[4] + acc;
            acc = SynMatrix8[i][3] * _sbSample[3] + acc;
            acc = SynMatrix8[i][2] * _sbSample[2] + acc;
            acc = SynMatrix8[i][1] * _sbSample[1] + acc;
            acc = SynMatrix8[i][0] * _sbSample[0] + acc;
            _v[_offset[i]] = acc >> StagedShift;
        }

        // Compute the samples（10 项点积：m0 走偶位、m1 走奇位）
        for (int idx = 0, i = 0; i < 8; i++, idx += 5)
        {
            int k = (i + 8) & 0xf;
            int oi = _offset[i];
            int ok = _offset[k];

            int acc = _v[ok + 9] * Proto8_80m1[idx + 4];
            acc = _v[oi + 8] * Proto8_80m0[idx + 4] + acc;
            acc = _v[ok + 7] * Proto8_80m1[idx + 3] + acc;
            acc = _v[oi + 6] * Proto8_80m0[idx + 3] + acc;
            acc = _v[ok + 5] * Proto8_80m1[idx + 2] + acc;
            acc = _v[oi + 4] * Proto8_80m0[idx + 2] + acc;
            acc = _v[ok + 3] * Proto8_80m1[idx + 1] + acc;
            acc = _v[oi + 2] * Proto8_80m0[idx + 1] + acc;
            acc = _v[ok + 1] * Proto8_80m1[idx + 0] + acc;
            acc = _v[oi + 0] * Proto8_80m0[idx + 0] + acc;

            short s = Clip16(acc >> StagedShift);
            pcmOut[sampleIdx++] = (byte)(s & 0xFF);
            pcmOut[sampleIdx++] = (byte)((s >> 8) & 0xFF);
        }
    }

    private static short Clip16(int s) =>
        s > 0x7FFF ? short.MaxValue : s < -0x8000 ? short.MinValue : (short)s;

    /// <summary>SBC CRC-8（多项式 0x1D，初值 0x0F，MSB-first；表按定义式生成避免抄表错，
    /// 金标准对拍单测兜底）。</summary>
    private static byte Crc8(ReadOnlySpan<byte> data)
    {
        byte crc = 0x0F;
        foreach (byte b in data)
            crc = CrcTable[crc ^ b];
        return crc;
    }

    /// <summary>CRC-8 查表（MSB-first，poly 0x1D）：t[i] = 8 次迭代 (c&lt;&lt;1) ^ (MSB ? 0x1D)。</summary>
    private static readonly byte[] CrcTable = BuildCrcTable();

    private static byte[] BuildCrcTable()
    {
        var table = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int c = i;
            for (int bit = 0; bit < 8; bit++)
                c = (c & 0x80) != 0 ? ((c << 1) ^ 0x1D) & 0xFF : (c << 1) & 0xFF;
            table[i] = (byte)c;
        }
        return table;
    }

    // ---- 固定参数表（libsbc sbc_tables.h 逐值转录；SS8/SN8 宏 = 算术右移 14 位） ----

    /// <summary>LOUDNESS 偏移表（sbc_offset8[4][8]，16kHz 用第 0 行）。</summary>
    private static readonly int[][] SbcOffset8 =
    {
        new[] { -2, 0, 0, 0, 0, 0, 0, 1 },
        new[] { -3, 0, 0, 0, 0, 0, 1, 2 },
        new[] { -4, 0, 0, 0, 0, 0, 1, 2 },
        new[] { -4, 0, 0, 0, 0, 0, 1, 2 },
    };

    /// <summary>合成原型滤波器偶相位（sbc_proto_8_80m0[40]，SS8 = ASR(v,14)）。</summary>
    private static readonly int[] Proto8_80m0 = BuildShiftedTable(
        0x00000000, 0xfe8d1970, 0xee979f00, 0x11686100,
        0x0172e690, 0xfff5bd1a, 0xfdf1c8d4, 0xeac182c0,
        0x0d9daee0, 0x00e530da, 0xffe9811d, 0xfd52986c,
        0xe7054ca0, 0x0a00d410, 0x006c1de4, 0xffdba705,
        0xfcbc98e8, 0xe3889d20, 0x06af2308, 0x000bb7db,
        0xffca00ed, 0xfc3fbb68, 0xe071bc00, 0x03bf7948,
        0xffc4e05c, 0xffb54b3b, 0xfbedadc0, 0xdde26200,
        0x0142291c, 0xff960e94, 0xff9f3e17, 0xfbd8f358,
        0xdbf79400, 0xff405e01, 0xff7d4914, 0xff8b1a31,
        0xfc1417b8, 0xdac7bb40, 0xfdbb828c, 0xff762170);

    /// <summary>合成原型滤波器奇相位（sbc_proto_8_80m1[40]）。</summary>
    private static readonly int[] Proto8_80m1 = BuildShiftedTable(
        0xff7c272c, 0xfcb02620, 0xda612700, 0xfcb02620,
        0xff7c272c, 0xff762170, 0xfdbb828c, 0xdac7bb40,
        0xfc1417b8, 0xff8b1a31, 0xff7d4914, 0xff405e01,
        0xdbf79400, 0xfbd8f358, 0xff9f3e17, 0xff960e94,
        0x0142291c, 0xdde26200, 0xfbedadc0, 0xffb54b3b,
        0xffc4e05c, 0x03bf7948, 0xe071bc00, 0xfc3fbb68,
        0xffca00ed, 0x000bb7db, 0x06af2308, 0xe3889d20,
        0xfcbc98e8, 0xffdba705, 0x006c1de4, 0x0a00d410,
        0xe7054ca0, 0xfd52986c, 0xffe9811d, 0x00e530da,
        0x0d9daee0, 0xeac182c0, 0xfdf1c8d4, 0xfff5bd1a);

    /// <summary>合成矩阵（synmatrix8[16][8]，SN8 = ASR(v, NPROTO8+1+EXTRA=14)）。</summary>
    private static readonly int[][] SynMatrix8 =
    {
        BuildShiftedTable(0x05a82798, 0xfa57d868, 0xfa57d868, 0x05a82798, 0x05a82798, 0xfa57d868, 0xfa57d868, 0x05a82798),
        BuildShiftedTable(0x0471ced0, 0xf8275a10, 0x018f8b84, 0x06a6d988, 0xf9592678, 0xfe70747c, 0x07d8a5f0, 0xfb8e3130),
        BuildShiftedTable(0x030fbc54, 0xf89be510, 0x07641af0, 0xfcf043ac, 0xfcf043ac, 0x07641af0, 0xf89be510, 0x030fbc54),
        BuildShiftedTable(0x018f8b84, 0xfb8e3130, 0x06a6d988, 0xf8275a10, 0x07d8a5f0, 0xf9592678, 0x0471ced0, 0xfe70747c),
        BuildShiftedTable(0, 0, 0, 0, 0, 0, 0, 0),
        BuildShiftedTable(0xfe70747c, 0x0471ced0, 0xf9592678, 0x07d8a5f0, 0xf8275a10, 0x06a6d988, 0xfb8e3130, 0x018f8b84),
        BuildShiftedTable(0xfcf043ac, 0x07641af0, 0xf89be510, 0x030fbc54, 0x030fbc54, 0xf89be510, 0x07641af0, 0xfcf043ac),
        BuildShiftedTable(0xfb8e3130, 0x07d8a5f0, 0xfe70747c, 0xf9592678, 0x06a6d988, 0x018f8b84, 0xf8275a10, 0x0471ced0),
        BuildShiftedTable(0xfa57d868, 0x05a82798, 0x05a82798, 0xfa57d868, 0xfa57d868, 0x05a82798, 0x05a82798, 0xfa57d868),
        BuildShiftedTable(0xf9592678, 0x018f8b84, 0x07d8a5f0, 0x0471ced0, 0xfb8e3130, 0xf8275a10, 0xfe70747c, 0x06a6d988),
        BuildShiftedTable(0xf89be510, 0xfcf043ac, 0x030fbc54, 0x07641af0, 0x07641af0, 0x030fbc54, 0xfcf043ac, 0xf89be510),
        BuildShiftedTable(0xf8275a10, 0xf9592678, 0xfb8e3130, 0xfe70747c, 0x018f8b84, 0x0471ced0, 0x06a6d988, 0x07d8a5f0),
        BuildShiftedTable(0xf8000000, 0xf8000000, 0xf8000000, 0xf8000000, 0xf8000000, 0xf8000000, 0xf8000000, 0xf8000000),
        BuildShiftedTable(0xf8275a10, 0xf9592678, 0xfb8e3130, 0xfe70747c, 0x018f8b84, 0x0471ced0, 0x06a6d988, 0x07d8a5f0),
        BuildShiftedTable(0xf89be510, 0xfcf043ac, 0x030fbc54, 0x07641af0, 0x07641af0, 0x030fbc54, 0xfcf043ac, 0xf89be510),
        BuildShiftedTable(0xf9592678, 0x018f8b84, 0x07d8a5f0, 0x0471ced0, 0xfb8e3130, 0xf8275a10, 0xfe70747c, 0x06a6d988),
    };

    /// <summary>把 libsbc 表的 32bit 十六进制按 ASR(v,14)（符号扩展算术右移）落到 int。</summary>
    private static int[] BuildShiftedTable(params uint[] hex)
    {
        var result = new int[hex.Length];
        for (int i = 0; i < hex.Length; i++)
            result[i] = unchecked((int)hex[i]) >> 14;
        return result;
    }
}
