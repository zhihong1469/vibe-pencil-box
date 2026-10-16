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

    // ===== 0x04 读 Git Hash（内部调试指令；只走串口/USB 直达轨，不走 BLE——定稿 2026-10-16 §8-1）=====

    /// <summary>
    /// `0x04` 读 Git Hash 请求：`[target]`（`LEN=0x04`）。canonical：笔端串口轨 `0x11`、基站 USB 轨 `0x00`
    /// （定稿 §3.2；串口轨设备对任意 target 恒应答自身，但按 canonical 填保帧面自解释）。
    /// </summary>
    byte[] EncodeReadHash(byte target);

    /// <summary>
    /// 解析 `0x04` 应答：`[STATUS][HASH40]`（`LEN=0x2C`——STATUS 1B + 40 字符 hex **ASCII**，非二进制）。
    /// `HASH` 不足 40 字符视为结构非法；尾部多余字节忽略（前向兼容）。`STATUS=0xFF` 表 hash 不可用
    /// （理论不发生——固件编译期内嵌，定稿 §3.5）。
    /// </summary>
    bool TryParseHashResp(ReadOnlySpan<byte> data, out byte status, out string hash);

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

    // ===== 0x61 内部调参（issues-pencil/01；仅串口轨 PC↔笔端 UART1，基站零感知）=====

    /// <summary>
    /// `0x61/01` 读请求：`[01][PARAM_ID]`（`LEN=0x05`）；`PARAM_ID=0xFF`（
    /// <see cref="TuningParamMode.ParamIdAll"/>）表全表读。
    /// </summary>
    byte[] EncodeTuningRead(byte paramId);

    /// <summary>`0x61/02` 写请求：`[02][PARAM_ID][VALUE4]`（`LEN=0x09`；VALUE4=float32 大端）</summary>
    byte[] EncodeTuningWrite(byte paramId, float value);

    /// <summary>
    /// 解析 `0x61/01` 单参读应答：`[01][STATUS][PARAM_ID][VALUE4]`（帧 LEN=`0x0A`——01 卷勘误 2：
    /// 原载 0x08 系笔误；PC 按字段结构式解析，不受该勘误影响）。
    /// 结构合法即 true（业务成败由 <paramref name="status"/> 表达）。
    /// </summary>
    bool TryParseTuningReadResp(ReadOnlySpan<byte> data, out byte status, out byte paramId, out float value);

    /// <summary>
    /// 解析 `0x61/01` 全表读应答：`[01][STATUS][FF][COUNT][ID+VALUE4]×N`（`LEN=0x07+5N`；6 参 → `0x25`，扩表 11 参 → `0x3E`）。
    /// `COUNT` 与载荷按 N×5 校验，尾部多余字节忽略（前向兼容）。
    /// </summary>
    bool TryParseTuningReadAllResp(ReadOnlySpan<byte> data, out byte status,
        out IReadOnlyList<TuningParamReading> items);

    /// <summary>
    /// 解析 `0x61/02` 写应答：`[02][STATUS][PARAM_ID][VALUE4]`（`LEN=0x0A`）。
    /// `STATUS=00` 时 VALUE4=生效值；`=02`（越界拒收）时 VALUE4=**写前旧值**（笔端不夹取）。
    /// </summary>
    bool TryParseTuningWriteResp(ReadOnlySpan<byte> data, out byte status, out byte paramId, out float effective);

    // ===== 0x62 空鼠遥测流（笔端 06 卷；仅串口轨，流帧无应答 PC 不回帧）=====

    /// <summary>`0x62/01` START：`[01][divider u8]`（`LEN=0x04`；divider 1=每帧 50Hz、2=25Hz…）</summary>
    byte[] EncodeStreamStart(byte divider);

    /// <summary>`0x62/00` STOP：空载荷（`LEN=0x03`；未开再停=00 幂等）</summary>
    byte[] EncodeStreamStop();

    /// <summary>
    /// 解析 START/STOP 应答：START=`[01][STATUS][echo divider]`、STOP=`[00][STATUS]`（载荷 ≤3B）。
    /// 回显 `SUBCMD` 须等于 <paramref name="expectSubCmd"/>（与请求配对消费）。
    /// </summary>
    bool TryParseStreamResp(ReadOnlySpan<byte> data, byte expectSubCmd, out byte status, out byte divider);

    /// <summary>
    /// 解析 `0x62` 流帧载荷（v2 定长 23B 大端，笔端 06 卷 §2；v1 21B 兼容收、TempImu=0）；
    /// 长度不符（含 START/STOP 应答混入）返回 false。
    /// </summary>
    bool TryParseTelemetryFrame(ReadOnlySpan<byte> data, out TelemetryFrame frame);

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

    // ================= 0x04 读 Git Hash（定稿 2026-10-16） =================

    /// <summary>HASH 字段宽度 = 40 字符 hex ASCII（与归档 CSV `full_commit_hash`、boot banner 同源）</summary>
    public const int HashAsciiLength = 40;

    public byte[] EncodeReadHash(byte target) =>
        CompanyFrame.Build(CompanyFrame.BaseCmdRequest, CompanyCmd.ReadHash, new[] { target });

    public bool TryParseHashResp(ReadOnlySpan<byte> data, out byte status, out string hash)
    {
        status = 0;
        hash = string.Empty;

        // STATUS(1) + HASH(40)；占位全零（40 个 '0'）也占满 40B
        if (data.Length < 1 + HashAsciiLength)
            return false;

        status = data[0];
        hash = ReadAsciiFixed(data.Slice(1, HashAsciiLength));
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

    // ================= 0x61 内部调参（补充 01 卷） =================

    public byte[] EncodeTuningRead(byte paramId) =>
        CompanyFrame.Build(CompanyFrame.BaseCmdRequest, CompanyCmd.TuningParam,
            new[] { TuningParamMode.Read, paramId });

    public byte[] EncodeTuningWrite(byte paramId, float value)
    {
        var data = new byte[2 + 4];
        data[0] = TuningParamMode.Write;
        data[1] = paramId;
        WriteSingleBigEndian(value, data.AsSpan(2, 4));

        return CompanyFrame.Build(CompanyFrame.BaseCmdRequest, CompanyCmd.TuningParam, data);
    }

    public bool TryParseTuningReadResp(ReadOnlySpan<byte> data, out byte status, out byte paramId, out float value)
    {
        status = 0;
        paramId = 0;
        value = 0;

        // MODE(01) STATUS PARAM_ID VALUE4
        if (data.Length < 6 || data[0] != TuningParamMode.Read)
            return false;

        status = data[1];
        paramId = data[2];
        value = ReadSingleBigEndian(data.Slice(3, 4));
        return true;
    }

    public bool TryParseTuningReadAllResp(ReadOnlySpan<byte> data, out byte status,
        out IReadOnlyList<TuningParamReading> items)
    {
        status = 0;
        items = Array.Empty<TuningParamReading>();

        // MODE(01) STATUS 0xFF COUNT [ID VALUE4]×N —— COUNT 须与载荷 N×5 对齐
        if (data.Length < 4 || data[0] != TuningParamMode.Read || data[2] != TuningParamMode.ParamIdAll)
            return false;

        status = data[1];
        int count = data[3];
        if (data.Length < 4 + count * 5)
            return false;

        var list = new List<TuningParamReading>(count);
        for (int i = 0; i < count; i++)
        {
            int at = 4 + i * 5;
            list.Add(new TuningParamReading(data[at], ReadSingleBigEndian(data.Slice(at + 1, 4))));
        }

        items = list;
        return true;
    }

    public bool TryParseTuningWriteResp(ReadOnlySpan<byte> data, out byte status, out byte paramId, out float effective)
    {
        status = 0;
        paramId = 0;
        effective = 0;

        // MODE(02) STATUS PARAM_ID VALUE4（生效值 / 越界=旧值）
        if (data.Length < 6 || data[0] != TuningParamMode.Write)
            return false;

        status = data[1];
        paramId = data[2];
        effective = ReadSingleBigEndian(data.Slice(3, 4));
        return true;
    }

    // ================= 0x62 空鼠遥测流（笔端 06 卷） =================

    public byte[] EncodeStreamStart(byte divider) =>
        CompanyFrame.Build(CompanyFrame.BaseCmdRequest, CompanyCmd.StreamTelemetry,
            new[] { StreamCmd.Start, divider });

    public byte[] EncodeStreamStop() =>
        CompanyFrame.Build(CompanyFrame.BaseCmdRequest, CompanyCmd.StreamTelemetry, new[] { StreamCmd.Stop });

    public bool TryParseStreamResp(ReadOnlySpan<byte> data, byte expectSubCmd, out byte status, out byte divider)
    {
        status = 0;
        divider = 0;

        // START: [01][STATUS][divider]（3B） / STOP: [00][STATUS]（2B）——回显子命令配对
        if (data.Length is < 2 or > 3 || data[0] != expectSubCmd)
            return false;

        status = data[1];
        if (expectSubCmd == StreamCmd.Start)
        {
            if (data.Length < 3)
                return false;
            divider = data[2];
        }

        return true;
    }

    public bool TryParseTelemetryFrame(ReadOnlySpan<byte> data, out TelemetryFrame frame)
    {
        if (!StreamCmd.IsFramePayload(data.Length))
        {
            frame = new TelemetryFrame(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            return false;
        }

        frame = new TelemetryFrame(
            (ushort)((data[0] << 8) | data[1]),
            data[2],
            ReadInt16BigEndian(data.Slice(3)),
            ReadInt16BigEndian(data.Slice(5)),
            ReadInt16BigEndian(data.Slice(7)),
            ReadInt16BigEndian(data.Slice(9)),
            ReadInt16BigEndian(data.Slice(11)),
            ReadInt16BigEndian(data.Slice(13)),
            ReadInt16BigEndian(data.Slice(15)),
            ReadInt16BigEndian(data.Slice(17)),
            (sbyte)data[19],
            (sbyte)data[20],
            data.Length >= StreamCmd.FramePayloadLength ? ReadInt16BigEndian(data.Slice(21)) : (short)0);
        return true;
    }

    /// <summary>i16 大端原码（流帧六轴/rate；补码直取）</summary>
    private static short ReadInt16BigEndian(ReadOnlySpan<byte> source) =>
        (short)((source[0] << 8) | source[1]);

    /// <summary>float32 → 4B 大端（VALUE4；黄金向量口径：0.27f=3E 8A 3D 71）</summary>
    private static void WriteSingleBigEndian(float value, Span<byte> destination)
    {
        int bits = BitConverter.SingleToInt32Bits(value);
        destination[0] = (byte)(bits >> 24);
        destination[1] = (byte)(bits >> 16);
        destination[2] = (byte)(bits >> 8);
        destination[3] = (byte)bits;
    }

    /// <summary>4B 大端 → float32（VALUE4）</summary>
    private static float ReadSingleBigEndian(ReadOnlySpan<byte> source) =>
        BitConverter.Int32BitsToSingle((source[0] << 24) | (source[1] << 16) | (source[2] << 8) | source[3]);

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
