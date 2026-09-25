// <copyright file="IFrameDecoder.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Ble;
using PencilBox.Core.Protocol.Company;

namespace PencilBox.Core.SerialScope;

/// <summary>
/// **F5 逻辑帧 → 人类可读文本**（05 §3.2 / §3.3 的第一个"绑定点"）。
///
/// 输入是**已定帧、未补零**的逻辑帧（定帧由 <see cref="StreamDemuxer"/> 完成），
/// 输出形如 `↓0x16 数据块 packh=12/224 packl=07`（`↓` 下行 / `↑` 上行）。
/// </summary>
public interface IFrameDecoder
{
    /// <summary>
    /// 解码一帧。识别不了（未知 `CMDTYPE` / 结构非法）返回 <c>null</c>
    /// —— **不冒充语义**（红线 4：宁可显示原始 HEX，也不编造解读）。
    /// </summary>
    string? Decode(ReadOnlySpan<byte> logicalFrame);
}

/// <summary>
/// 公司 V0.9（F5）帧解码器：**复用既有 <see cref="ICompanyCodec"/>**（同一套权威口径，
/// 避免"日志解码"与"命令收发"两处解析各写一遍而漂移）。
///
/// 有状态的一处：记住最近一次 `0x16/01` 的 `FILELEN`，把块进度渲染成 `packh=%d/%d`
/// （05 §3.3 / §3.4 要求 `0x16 升级块 %d/%d`）。跨"块请求/数据片/进度"三帧共用。
/// </summary>
public sealed class CompanyFrameDecoder : IFrameDecoder
{
    /// <summary>升级块大小（公司 §3.2：每块 1024B）</summary>
    private const int BlockBytes = 1024;

    private readonly ICompanyCodec _codec;

    /// <summary>会话总块数（0 = 未知；由 `0x16/01` 的 `FILELEN` 推出）</summary>
    private int _totalBlocks;

    public CompanyFrameDecoder(ICompanyCodec? codec = null) => _codec = codec ?? new CompanyCodec();

    public string? Decode(ReadOnlySpan<byte> logicalFrame)
    {
        if (!CompanyFrame.TryParse(logicalFrame, out var baseCmd, out var cmdType, out var data))
            return null;

        bool uplink = baseCmd == CompanyFrame.BaseCmdResponse;
        string arrow = uplink ? "↑" : "↓";

        return cmdType switch
        {
            CompanyCmd.ReadVersion => arrow + DecodeReadVersion(uplink, data),
            CompanyCmd.PairInfo => arrow + DecodePairInfo(uplink, data),
            CompanyCmd.PairEnter => arrow + DecodePairEnter(uplink, data),
            CompanyCmd.Ota => arrow + DecodeOta(uplink, data),
            _ => null
        };
    }

    // ===== 0x03 读型号/版本 =====

    private string DecodeReadVersion(bool uplink, ReadOnlySpan<byte> data)
    {
        if (!uplink)
            return "0x03 读版本";

        if (!_codec.TryParseVersionResp(data, out var info))
            return "0x03 应答（结构非法）";

        if (info.Status != CompanyStatus.Ok)
            return $"0x03 应答 STA={info.Status:X2}（设备未定义型号/版本，原样显示）";

        return $"0x03 应答 STA={info.Status:X2} MODEL={info.Model:X2}({ModelText(info.Model)}) " +
               $"SVER={info.SverMajor}.{info.SverMinor}.{info.SverPatch}";
    }

    // ===== 0x51 配对信息读写 =====

    private string DecodePairInfo(bool uplink, ReadOnlySpan<byte> data)
    {
        byte param = data.Length > 0 ? data[0] : (byte)0x00;

        if (!uplink)
            return param == CompanyParam.PairWrite
                ? "0x51 写配对信息" + DecodePairWritePayload(data)
                : $"0x51 读配对信息 NO={(data.Length > 1 ? data[1] : 0)}";

        if (param == CompanyParam.PairWrite)
        {
            return _codec.TryParsePairWriteResp(data, out var wStatus, out var wNo)
                ? $"0x51 写应答 STA={wStatus:X2} NO={wNo}"
                : "0x51 写应答（结构非法）";
        }

        if (!_codec.TryParsePairReadResp(data, out var status, out var info))
            return "0x51 读应答（结构非法）";

        return info == null
            ? $"0x51 读应答 STA={status:X2}（读取失败/无配对信息）"
            : $"0x51 读应答 STA={status:X2} NO={info.No} MAC={MacAddress.FormatDisplay(info.MacDisplay6)} " +
              $"NAME=\"{info.BtName}\"";
    }

    /// <summary>下行 `0x51` 写的负载（`PARAM(02) NO(1) MAC(6) NAME(32)`）—— 只呈现有用字段，不回显整段补零</summary>
    private static string DecodePairWritePayload(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2 + 6)
            return "（负载不完整）";

        byte no = data[1];
        var mac = MacAddress.FormatDisplay(data.Slice(2, 6));
        var name = data.Length >= 8 + CompanyCodec.BtNameLength
            ? ReadAsciiZ(data.Slice(8, CompanyCodec.BtNameLength))
            : string.Empty;

        return $" NO={no} MAC={mac} NAME=\"{name}\"";
    }

    // ===== 0x52 进配对 =====

    private string DecodePairEnter(bool uplink, ReadOnlySpan<byte> data)
    {
        if (!uplink)
            return "0x52 进配对";

        if (!_codec.TryParsePairEnterResp(data, out var status, out var info))
            return "0x52 应答（结构非法）";

        return status switch
        {
            PairEnterStatus.Entered => "0x52 应答 STA=01 已进入配对，等待笔端",
            PairEnterStatus.Success => info == null
                ? "0x52 应答 STA=02 配对成功"
                : $"0x52 应答 STA=02 配对成功 MAC={MacAddress.FormatDisplay(info.MacDisplay6)} " +
                  $"NAME=\"{info.BtName}\"",
            PairEnterStatus.Failed => "0x52 应答 STA=03 配对失败",
            _ => $"0x52 应答 STA={status:X2}"
        };
    }

    // ===== 0x16 升级（拉模式）=====

    private string DecodeOta(bool uplink, ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return "0x16（空负载）";

        return data[0] switch
        {
            0x01 => DecodeOtaSession(uplink, data),
            0x07 => DecodeOtaChunk(uplink, data),
            0x0A => DecodeOtaBlockRequest(data),
            0x20 => DecodeOtaProgress(data),
            _ => $"0x16 MODE={data[0]:X2}（未知子类型）"
        };
    }

    /// <summary>`0x16/01` 开始/退出：`[01][DOWNTYPE][DCMD][HVER][SVER1~3][CRC16(2)][FILELEN(4)]`，大端</summary>
    private string DecodeOtaSession(bool uplink, ReadOnlySpan<byte> data)
    {
        if (!_codec.TryParseOtaSessionResp(data, out var respDowntype, out var respDcmd) || data.Length < 3)
            return "0x16 会话（结构非法）";

        if (uplink)
        {
            var text = respDcmd switch
            {
                0x01 => "允许（进入了拉模式）",
                0x02 => "跳过（版本相同，无需升级）",
                0x00 => "退出回显",
                0xFF => "拒绝",
                _ => $"DCMD={respDcmd:X2}"
            };
            return $"0x16 会话应答 DCMD={respDcmd:X2}({text}) DOWNTYPE={DownTypeText(respDowntype)}";
        }

        if (data.Length < 13)
            return $"0x16 开始 DCMD={respDcmd:X2}（负载不完整，应为 13B）";

        byte hver = data[3];
        string sver = $"{data[4]}.{data[5]}.{data[6]}";
        ushort crc = (ushort)((data[7] << 8) | data[8]);
        uint fileLen = ((uint)data[9] << 24) | ((uint)data[10] << 16) | ((uint)data[11] << 8) | data[12];

        // 会话开始 ⇒ 记住总块数，供后续 0x07/0x0A/0x20 渲染成 `packh=x/total`
        if (respDcmd == 0x01 && fileLen > 0)
            _totalBlocks = (int)((fileLen + BlockBytes - 1) / BlockBytes);

        var kind = respDcmd == 0x00 ? "退出" : "开始";
        return $"0x16 {kind} DCMD={respDcmd:X2} DOWNTYPE={DownTypeText(respDowntype)} HVER={hver:X2} " +
               $"SVER={sver} FILELEN={fileLen} 块 {_totalBlocks} CRC=0x{crc:X4}";
    }

    /// <summary>`0x16/07` 数据片：`[07][DOWNTYPE][DOWNID][PACKH][PACKL][DATA48]`</summary>
    private string DecodeOtaChunk(bool uplink, ReadOnlySpan<byte> data)
    {
        if (data.Length < 5)
            return "0x16 数据片（结构非法）";

        return $"0x16 数据块 packh={Pack(data[3])} packl={data[4]:X2}" +
               (uplink ? "（上行出现 07，异常）" : string.Empty);
    }

    /// <summary>`0x16/0A` 块请求：`[0A][DOWNTYPE][DOWNID][PACKH][PACKL=00]`</summary>
    private string DecodeOtaBlockRequest(ReadOnlySpan<byte> data)
    {
        if (!_codec.TryParseOtaBlockRequest(data, out var downtype, out _, out var packH))
            return "0x16 块请求（结构非法）";

        return $"0x16 请求块 packh={Pack(packH)} DOWNTYPE={DownTypeText(downtype)}";
    }

    /// <summary>`0x16/20` 进度/终态：`[20][DOWNTYPE][DCMD][PACKH]`（02 完成 / 03 失败）</summary>
    private string DecodeOtaProgress(ReadOnlySpan<byte> data)
    {
        if (!_codec.TryParseOtaProgress(data, out var downtype, out var dcmd, out var packH))
            return "0x16 进度（结构非法）";

        // ⚠️ `DCMD=03` 时 `PACKH` 是**失败原因**、不是块号（Codec 口径）——不能当进度渲染，否则会显示成"14/224"这种假进度
        if (dcmd == 0x03)
            return $"0x16 进度 终态=失败（原因={packH:X2}） DOWNTYPE={DownTypeText(downtype)}";

        var text = dcmd == 0x02 ? "完成" : $"DCMD={dcmd:X2}";
        return $"0x16 进度 packh={Pack(packH)} 终态={text} DOWNTYPE={DownTypeText(downtype)}";
    }

    // ===== 渲染小工具 =====

    /// <summary>块号：已知总数时渲染 `12/224`，未知时只给 `12`</summary>
    private string Pack(byte packH) => _totalBlocks > 0 ? $"{packH}/{_totalBlocks}" : packH.ToString();

    /// <summary>`DOWNTYPE` 会话目标（公司 §3.2 / E23）</summary>
    private static string DownTypeText(byte downtype) => downtype switch
    {
        (byte)Downtype.BaseStation => "00/基站",
        (byte)Downtype.Pen => "01/笔端",
        _ => $"{downtype:X2}"
    };

    /// <summary>`MODEL` 位段类别（骨架 §6.2 P5：bit7-4 类别）</summary>
    private static string ModelText(byte model) => (model >> 4) switch
    {
        0x0 => "基站",
        0x1 => "笔端",
        0xF => "通用测试",
        _ => "未知类别"
    };

    /// <summary>ASCII 读定长字段：遇 0 截断</summary>
    private static string ReadAsciiZ(ReadOnlySpan<byte> source)
    {
        int end = source.IndexOf((byte)0x00);
        if (end < 0)
            end = source.Length;
        return System.Text.Encoding.ASCII.GetString(source[..end]).TrimEnd('\0', ' ');
    }
}
