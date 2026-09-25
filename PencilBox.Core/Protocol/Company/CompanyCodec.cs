// <copyright file="CompanyCodec.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Text;

namespace PencilBox.Core.Protocol.Company;

/// <summary>
/// 公司 V0.9 命令层的**编解码约定**（纯函数：对象 → 字节，不判状态、不做重试、不发 IO）。
/// 帧底座的组帧/解帧见 <see cref="CompanyFrame"/>（静态，无实例状态）。
/// </summary>
public interface ICompanyCodec
{
    /// <summary>
    /// `0x03` 读型号/版本请求。请求第 4 字节 = <paramref name="target"/>（**G14 已冻结，issues/05 §1-D**）：
    /// 串口轨恒 `0x00`（黄金向量不变）；无线轨（B1 透传）问**笔端**填
    /// <see cref="CompanyConfig.PenTargetClass"/>（`0x10`/`0x11`）、问**基站**填 `0x00`（基站自答不转发）。
    /// </summary>
    byte[] EncodeReadVersion(byte target = 0x00);

    /// <summary>解析 `0x03` 应答</summary>
    bool TryParseVersionResp(ReadOnlySpan<byte> data, out VersionInfo info);

    /// <summary>`0x51` 读配对信息请求（`PARAM=01` + `NO`）</summary>
    byte[] EncodePairRead(byte no);

    /// <summary>`0x51` 写配对信息请求（`PARAM=02` + `NO` + `BT_MAC(6)` + `BT_NAME(32)`；`LEN=0x2B`）</summary>
    byte[] EncodePairWrite(byte no, ReadOnlySpan<byte> macDisplay6, string btName);

    /// <summary>解析 `0x51` 读应答（`STATUS=00` 时回填 <paramref name="info"/>）</summary>
    bool TryParsePairReadResp(ReadOnlySpan<byte> data, out byte status, out PairInfo? info);

    /// <summary>解析 `0x51` 写应答（`STATUS` / `NO`）</summary>
    bool TryParsePairWriteResp(ReadOnlySpan<byte> data, out byte status, out byte no);

    /// <summary>`0x52` 进配对请求</summary>
    byte[] EncodePairEnter();

    /// <summary>解析 `0x52` 应答（`02` 时回填配对到的笔端 <paramref name="info"/>）</summary>
    bool TryParsePairEnterResp(ReadOnlySpan<byte> data, out byte status, out PairInfo? info);

    // ===== 0x51 PARAM=03 清绑定（协议补充 01；仅笔端串口轨，基站轨禁发）=====

    /// <summary>`0x51/03` 清绑定请求（`LEN=0x04`：`61 01 51 03`）</summary>
    byte[] EncodePairClear();

    /// <summary>
    /// 解析 `0x51/03` 应答（`[PARAM=03][STATUS]`；`00`=受理（幂等）/`FF`=升级会话中拒绝）。
    /// 结构合法即 true（业务成败由 <paramref name="status"/> 表达，同 0x51 读口径）。
    /// </summary>
    bool TryParsePairClearResp(ReadOnlySpan<byte> data, out byte status);

    // ===== 0x53 状态上行（协议补充 05；双向一命令：61 拉 + E1 推，仅基站 USB 轨）=====

    /// <summary>`0x53` 拉取请求（`LEN=0x04`：`61 01 53 01`；基站本地槽即答，不打扰笔端）</summary>
    byte[] EncodePenStatusPull();

    /// <summary>
    /// 解析 `0x53` 应答/推送（共用格式）：`[STATUS][BYTE2][槽区 TLV...]`——`LEN=5+S×2`（S=2 → 9）。
    /// `STATUS=02`（笔未连接，断连推送）无槽区（`LEN=4`，data 仅 1B）合法。
    /// 槽区为 `2B` 整数倍否则视为结构损坏；未知 `state_id` 原样收进快照（前向兼容）。
    /// </summary>
    bool TryParsePenStatusResp(ReadOnlySpan<byte> data, out PenStatusSnapshot snapshot);

    // ===== 0x54 统一TX 转发信封（issues-pencil/12 §2.5；语义单一来源 = 统一TX 规范 §7）=====

    /// <summary>
    /// `0x54` PEN_CMD 信封请求：载荷 `[cmd_id][param...]` **原样装封**（本端只搬运不解释）。
    /// 载荷 1~<see cref="CompanyCodec.EnvelopePayloadMax"/>B（超限抛参数错——**编码前置校验，
    /// 不产帧**；上限 57B = USB 64B 定长报告倒推，pc-issues/03 C1/P-1）。
    /// </summary>
    byte[] EncodePenCmd(ReadOnlySpan<byte> payload);

    /// <summary>
    /// 解析 `0x54` 信封应答：`[STATUS][cmd_id][status][data...]`（双层，pc-issues/03 C3）。
    /// `STATUS≠00` 时无 RSP 载荷（`LEN=0x04` 合法）；`STATUS=00` 时须含 RSP 头
    /// `cmd_id+status`（缺 → 结构非法）。<paramref name="rspData"/> = RSP 内嵌 `data...`。
    /// </summary>
    bool TryParsePenCmdResponse(ReadOnlySpan<byte> data, out byte status, out byte cmdId,
        out byte penStatus, out byte[] rspData);

    // ===== 0x16 固件升级（拉模式；字段序＝公司 §3.2 / 正文 上位机/03 §②）=====

    /// <summary>
    /// `0x16/01` 开始 / 退出帧（`LEN=0x10`，总 20B）：
    /// `[01][DOWNTYPE][DCMD][HVER][SVER1~3][CRC16(2)][FILELEN(4)]`，大端。
    /// </summary>
    byte[] EncodeOtaSession(Downtype downtype, byte dcmd, byte hver, byte sver1, byte sver2, byte sver3,
        ushort crc16, uint fileLen);

    /// <summary>解析 `0x16/01` 应答（回显；`DCMD`：01 允许 / 02 跳过 / 00 退出回显 / FF 拒绝）</summary>
    bool TryParseOtaSessionResp(ReadOnlySpan<byte> data, out byte downtype, out byte dcmd);

    /// <summary>
    /// `0x16/07` 数据片（`LEN=0x38`，总 60B）：
    /// `[07][DOWNTYPE][DOWNID][PACKH][PACKL][DATA48]`；`DOWNTYPE` 回显会话目标（E23）。
    /// </summary>
    byte[] EncodeOtaChunk(Downtype downtype, byte downId, byte packH, byte packL, ReadOnlySpan<byte> data48);

    /// <summary>解析 `0x16/0A` 块请求（`[0A][DOWNTYPE][DOWNID][PACKH][PACKL=00]`）</summary>
    bool TryParseOtaBlockRequest(ReadOnlySpan<byte> data, out byte downtype, out byte downId, out byte packH);

    /// <summary>解析 `0x16/20` 进度/终态（`[20][DOWNTYPE][DCMD][PACKH]`；02 完成 / 03 失败（PACKH=原因））</summary>
    bool TryParseOtaProgress(ReadOnlySpan<byte> data, out byte downtype, out byte dcmd, out byte packH);
}

/// <summary>
/// 公司 V0.9 命令编解码实现（`0x03` / `0x51` / `0x52`）。
/// `0x16` 升级载荷（分片/offset/末块截断/CRC16）在 S2-3 落地时补入本类。
/// </summary>
public sealed class CompanyCodec : ICompanyCodec
{
    /// <summary>`BT_NAME` 字段宽度（骨架 §6.1：32B ASCII，0 结束）</summary>
    public const int BtNameLength = 32;

    /// <summary>`0x51` 读应答中 `BT_NAME` 的起始偏移：`PARAM(1) STATUS(1) NO(1) MAC(6)`</summary>
    private const int PairReadNameOffset = 1 + 1 + 1 + 6;

    /// <summary>`0x52` 答 02 中 `BT_NAME` 的起始偏移：`STATUS(1) MAC(6)`</summary>
    private const int PairEnterNameOffset = 1 + 6;

    /// <summary>
    /// `0x54` 信封**载荷上限 = 57B**（pc-issues/03 C1/P-1：USB 64B 定长报告 − 帧头 4 − 信封头 3；
    /// 全局单一口径，串口形态不放宽——一份黄金向量、一种行为）。
    /// </summary>
    public const int EnvelopePayloadMax = 57;

    /// <summary>`0x54` 应答中**笔端 RSP 载荷上限 = 56B**（64 − 4 − `E1 01 54 STATUS` 4）；统一TX 命令设计约束</summary>
    public const int EnvelopeRspPayloadMax = 56;

    public byte[] EncodeReadVersion(byte target = 0x00) =>
        CompanyFrame.Build(CompanyFrame.BaseCmdRequest, CompanyCmd.ReadVersion, new[] { target });

    public bool TryParseVersionResp(ReadOnlySpan<byte> data, out VersionInfo info)
    {
        info = default!;

        // 最小应答：STATUS MODEL SVER1 SVER2 SVER3（LEN=0x08；带 24B RESERVED 时 LEN=0x20，尾部忽略）
        if (data.Length < 5)
            return false;

        info = new VersionInfo(data[0], data[1], data[2], data[3], data[4]);
        return true;
    }

    public byte[] EncodePairRead(byte no) =>
        CompanyFrame.Build(CompanyFrame.BaseCmdRequest, CompanyCmd.PairInfo,
            new[] { CompanyParam.PairRead, no });

    public byte[] EncodePairWrite(byte no, ReadOnlySpan<byte> macDisplay6, string btName)
    {
        if (macDisplay6.Length != 6)
            throw new ArgumentException($"BT_MAC 必须 6 字节（大端显示序），实得 {macDisplay6.Length}", nameof(macDisplay6));

        var data = new byte[2 + 6 + BtNameLength];
        data[0] = CompanyParam.PairWrite;
        data[1] = no;
        macDisplay6.CopyTo(data.AsSpan(2, 6));
        WriteAsciiFixed(btName, data.AsSpan(8, BtNameLength));

        return CompanyFrame.Build(CompanyFrame.BaseCmdRequest, CompanyCmd.PairInfo, data);
    }

    public bool TryParsePairReadResp(ReadOnlySpan<byte> data, out byte status, out PairInfo? info)
    {
        status = 0;
        info = null;

        // PARAM(01) STATUS NO MAC6 [NAME32]
        if (data.Length < 1 + 1 + 1 + 6)
            return false;

        status = data[1];
        if (status != CompanyStatus.Ok)
            return true;   // 结构合法、业务失败（STATUS=FF）：由调用方决定提示，不算解析失败

        byte no = data[2];
        var mac = data.Slice(3, 6).ToArray();
        var name = data.Length >= PairReadNameOffset + BtNameLength
            ? ReadAsciiFixed(data.Slice(PairReadNameOffset, BtNameLength))
            : string.Empty;

        info = new PairInfo(no, mac, name);
        return true;
    }

    public bool TryParsePairWriteResp(ReadOnlySpan<byte> data, out byte status, out byte no)
    {
        status = 0;
        no = 0;

        // PARAM(02) STATUS NO
        if (data.Length < 3)
            return false;

        status = data[1];
        no = data[2];
        return true;
    }

    public byte[] EncodePairEnter() =>
        CompanyFrame.Build(CompanyFrame.BaseCmdRequest, CompanyCmd.PairEnter,
            new[] { CompanyParam.PairEnter });

    public bool TryParsePairEnterResp(ReadOnlySpan<byte> data, out byte status, out PairInfo? info)
    {
        status = 0;
        info = null;

        if (data.Length < 1)
            return false;

        status = data[0];
        if (status != PairEnterStatus.Success)
            return true;   // 01 已进入 / 03 失败：结构合法

        if (data.Length < 1 + 6)
            return true;   // 成功但缺 MAC：按"无附带信息"处理，不算解析失败

        var mac = data.Slice(1, 6).ToArray();
        var name = data.Length >= PairEnterNameOffset + BtNameLength
            ? ReadAsciiFixed(data.Slice(PairEnterNameOffset, BtNameLength))
            : string.Empty;

        info = new PairInfo(0x00, mac, name);
        return true;
    }

    // ================= 0x54 统一TX 转发信封 =================

    public byte[] EncodePenCmd(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1)
            throw new ArgumentException("信封载荷至少 1B（cmd_id）", nameof(payload));
        if (payload.Length > EnvelopePayloadMax)
            throw new ArgumentException(
                $"信封载荷超限：{payload.Length}B > {EnvelopePayloadMax}B（C1：USB 64B 定长报告倒推；pc-issues/03 P-1）",
                nameof(payload));

        return CompanyFrame.Build(CompanyFrame.BaseCmdRequest, CompanyCmd.PenCmd, payload);
    }

    public bool TryParsePenCmdResponse(ReadOnlySpan<byte> data, out byte status, out byte cmdId,
        out byte penStatus, out byte[] rspData)
    {
        status = 0;
        cmdId = 0;
        penStatus = 0;
        rspData = Array.Empty<byte>();

        if (data.Length < 1)
            return false;

        status = data[0];
        if (status != CompanyStatus.Ok)
            return true;   // 终结层失败：无 RSP 载荷（LEN=0x04），结构合法；归因归调用方

        if (data.Length < 3)
            return false;  // STATUS=00 却缺 RSP 头（cmd_id+status）：结构非法

        cmdId = data[1];
        penStatus = data[2];
        rspData = data.Slice(3).ToArray();
        return true;
    }

    // ================= 0x51 PARAM=03 清绑定（补充 01） =================

    public byte[] EncodePairClear() =>
        CompanyFrame.Build(CompanyFrame.BaseCmdRequest, CompanyCmd.PairInfo,
            new[] { CompanyParam.PairClear });

    public bool TryParsePairClearResp(ReadOnlySpan<byte> data, out byte status)
    {
        status = 0;

        // PARAM(03) STATUS
        if (data.Length < 2 || data[0] != CompanyParam.PairClear)
            return false;

        status = data[1];
        return true;
    }

    // ================= 0x53 状态上行（补充 05） =================

    public byte[] EncodePenStatusPull() =>
        CompanyFrame.Build(CompanyFrame.BaseCmdRequest, CompanyCmd.PenStatus,
            new[] { CompanyParam.PenStatusPull });

    public bool TryParsePenStatusResp(ReadOnlySpan<byte> data, out PenStatusSnapshot snapshot)
    {
        snapshot = new PenStatusSnapshot(0, 0, Array.Empty<PenStatusSlot>());

        // STATUS=02 断连推送：data 仅 1B（无 BYTE2/槽区）
        if (data.Length == 1)
        {
            snapshot = new PenStatusSnapshot(data[0], 0, Array.Empty<PenStatusSlot>());
            return true;
        }

        // 正常：STATUS BYTE2 [state_id][value]×S（S = (LEN-2)/2，须偶数）
        if (data.Length < 2 || (data.Length - 2) % 2 != 0)
            return false;

        var slots = new List<PenStatusSlot>((data.Length - 2) / 2);
        for (int i = 2; i + 1 < data.Length; i += 2)
            slots.Add(new PenStatusSlot(data[i], data[i + 1]));

        snapshot = new PenStatusSnapshot(data[0], data[1], slots);
        return true;
    }

    // ================= 0x16 固件升级（拉模式） =================

    public byte[] EncodeOtaSession(Downtype downtype, byte dcmd, byte hver, byte sver1, byte sver2, byte sver3,
        ushort crc16, uint fileLen)
    {
        var data = new byte[13];
        data[0] = 0x01;                        // MODE=01（会话）
        data[1] = (byte)downtype;
        data[2] = dcmd;
        data[3] = hver;
        data[4] = sver1;
        data[5] = sver2;
        data[6] = sver3;
        data[7] = (byte)(crc16 >> 8);          // 大端
        data[8] = (byte)crc16;
        data[9] = (byte)(fileLen >> 24);
        data[10] = (byte)(fileLen >> 16);
        data[11] = (byte)(fileLen >> 8);
        data[12] = (byte)fileLen;

        return CompanyFrame.Build(CompanyFrame.BaseCmdRequest, CompanyCmd.Ota, data);
    }

    public bool TryParseOtaSessionResp(ReadOnlySpan<byte> data, out byte downtype, out byte dcmd)
    {
        downtype = 0;
        dcmd = 0;

        if (data.Length < 3 || data[0] != 0x01)
            return false;

        downtype = data[1];
        dcmd = data[2];
        return true;
    }

    public byte[] EncodeOtaChunk(Downtype downtype, byte downId, byte packH, byte packL, ReadOnlySpan<byte> data48)
    {
        if (data48.Length != 48)
            throw new ArgumentException($"数据片必须 48B（末块截断后补零），实得 {data48.Length}", nameof(data48));

        var data = new byte[5 + 48];
        data[0] = 0x07;                        // MODE=07（数据片）
        data[1] = (byte)downtype;              // 回显会话目标（E23）
        data[2] = downId;
        data[3] = packH;
        data[4] = packL;
        data48.CopyTo(data.AsSpan(5));

        return CompanyFrame.Build(CompanyFrame.BaseCmdRequest, CompanyCmd.Ota, data);
    }

    public bool TryParseOtaBlockRequest(ReadOnlySpan<byte> data, out byte downtype, out byte downId, out byte packH)
    {
        downtype = 0;
        downId = 0;
        packH = 0;

        if (data.Length < 5 || data[0] != 0x0A)
            return false;

        downtype = data[1];
        downId = data[2];
        packH = data[3];
        return true;
    }

    public bool TryParseOtaProgress(ReadOnlySpan<byte> data, out byte downtype, out byte dcmd, out byte packH)
    {
        downtype = 0;
        dcmd = 0;
        packH = 0;

        if (data.Length < 4 || data[0] != 0x20)
            return false;

        downtype = data[1];
        dcmd = data[2];
        packH = data[3];
        return true;
    }

    /// <summary>ASCII 写入定长字段：截断到定长、其余补 0（0 结束）</summary>
    private static void WriteAsciiFixed(string? text, Span<byte> destination)
    {
        destination.Clear();
        if (string.IsNullOrEmpty(text))
            return;

        var bytes = Encoding.ASCII.GetBytes(text);
        int copy = Math.Min(bytes.Length, destination.Length);
        bytes.AsSpan(0, copy).CopyTo(destination);
    }

    /// <summary>ASCII 读取定长字段：遇 0 截断，去除尾部空白</summary>
    private static string ReadAsciiFixed(ReadOnlySpan<byte> source)
    {
        int end = source.IndexOf((byte)0x00);
        if (end < 0)
            end = source.Length;

        var text = Encoding.ASCII.GetString(source[..end]);
        return text.TrimEnd('\0', ' ');
    }
}
