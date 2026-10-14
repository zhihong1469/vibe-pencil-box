// <copyright file="CompanyModels.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Protocol.Company;

/// <summary>公司 V0.9 `CMDTYPE`（本端接入用到的命令族；`0x16` 升级在 S2-3 落地）</summary>
public static class CompanyCmd
{
    /// <summary>读被查询设备自身型号/版本</summary>
    public const byte ReadVersion = 0x03;

    /// <summary>配对信息读写（`PARAM` 01 读 / 02 写 / 03 清绑定——补充 01，仅笔端串口轨）</summary>
    public const byte PairInfo = 0x51;

    /// <summary>进入配对模式（基站实现；笔端不做）</summary>
    public const byte PairEnter = 0x52;

    /// <summary>
    /// 固件升级（拉模式）
    /// </summary>
    public const byte Ota = 0x16;

    /// <summary>
    /// 状态上行 `PEN_STATUS`（协议补充 05，`0x53` 由 issues-pencil/09 让位定值）：
    /// 基站把当前连接笔端的状态快照**原样**上行 PC——`61` 拉（基站本地槽即答）+ `E1` 推（触发策略主动上报），
    /// 双向一命令；**只搬运不解析**（与 `0x54` 信封同构），笔端零改动。仅基站（USB）轨。
    /// </summary>
    public const byte PenStatus = 0x53;

    /// <summary>
    /// 统一TX 转发信封 `PEN_CMD`（issues-pencil/12 §2.5 定值 `0x54`——09 号 `0x53` 让位给状态上行后
    /// 成对定案：53 上行 / 54 下行）。语义单一来源 = 统一TX 规范 §7，本端只搬运字节不解释（12 号 E3）。
    /// </summary>
    public const byte PenCmd = 0x54;

    /// <summary>
    /// 内部调参（issues-pencil/01：`0x61/01` 读 · `0x61/02` 写，VALUE4=float32 大端）。
    /// **仅串口轨 PC↔笔端 UART1 直连**（基站零感知不转发）；数值与 <see cref="CompanyFrame.BaseCmdRequest"/>
    /// 的 `0x61` 重叠纯属 CMDTYPE 命名空间巧合，互不冲突。
    /// </summary>
    public const byte TuningParam = 0x61;
}

/// <summary>`PARAM`（`CMDTYPE` 之后的首字节）</summary>
public static class CompanyParam
{
    /// <summary>`0x51` 读</summary>
    public const byte PairRead = 0x01;

    /// <summary>`0x51` 写</summary>
    public const byte PairWrite = 0x02;

    /// <summary>`0x51` 清绑定（补充 01：笔端删自身 bond → 进配对名视窗；**仅串口轨**，基站轨禁发）</summary>
    public const byte PairClear = 0x03;

    /// <summary>`0x52` 进配对</summary>
    public const byte PairEnter = 0x01;

    /// <summary>`0x53` 拉取快照（基站本地槽缓存即答，不打扰笔端——补充 05 §1）</summary>
    public const byte PenStatusPull = 0x01;
}

/// <summary>`0x61` 内部调参请求首字节 `MODE`（issues-pencil/01 §3）</summary>
public static class TuningParamMode
{
    /// <summary>读：`[01][PARAM_ID]`；`PARAM_ID=0xFF` 表全表读</summary>
    public const byte Read = 0x01;

    /// <summary>写：`[02][PARAM_ID][VALUE4]`</summary>
    public const byte Write = 0x02;

    /// <summary>读全表的 `PARAM_ID` 通配值</summary>
    public const byte ParamIdAll = 0xFF;
}

/// <summary>`0x61` 应答 `STATUS`（issues-pencil/01 §4）</summary>
public static class TuningStatus
{
    /// <summary>成功（写应答 VALUE4=生效值）</summary>
    public const byte Ok = 0x00;

    /// <summary>未知参数 ID</summary>
    public const byte UnknownId = 0x01;

    /// <summary>越界拒绝（不夹取，回写前旧值）</summary>
    public const byte OutOfRange = 0x02;

    /// <summary>OTA 升级会话进行中（仅写被拒；读不受影响）</summary>
    public const byte OtaBusy = 0x03;
}

/// <summary>`0x03` / `0x51` 应答的 `STATUS`（骨架 §6.1/§6.2）</summary>
public static class CompanyStatus
{
    /// <summary>成功</summary>
    public const byte Ok = 0x00;

    /// <summary>`0x51` 写参数错；**笔端对写恒回此值**（不支持写，不视为可重试失败）</summary>
    public const byte ParamError = 0x01;

    /// <summary>`0x51` 写 NO 越界</summary>
    public const byte NoOutOfRange = 0x02;

    /// <summary>`0x03`/`0x51` 读失败（未定义/其它）</summary>
    public const byte Fail = 0xFF;
}

/// <summary>`0x52` 进配对应答的 `STATUS`（骨架 §6.3：先回 01，延时再回 02/03）</summary>
public static class PairEnterStatus
{
    /// <summary>已进入配对模式，等待笔端</summary>
    public const byte Entered = 0x01;

    /// <summary>配对成功（帧内带配对到的笔端 MAC/名称）</summary>
    public const byte Success = 0x02;

    /// <summary>配对失败</summary>
    public const byte Failed = 0x03;
}

/// <summary>
/// `0x54` 信封应答的**终结层 STATUS**（issues-pencil/12 §2.5：无线轨 = 基站转发结果；
/// 串口先行轨 = 笔端本地终结同表）。区别于其后内嵌的**笔端 RSP status**（双层，pc-issues/03 C3）。
/// </summary>
public static class PenCmdStatus
{
    /// <summary>成功（已收到笔端 RSP，其后为 RSP 原文）</summary>
    public const byte Ok = 0x00;

    /// <summary>参数错误（含载荷超限 >57B）</summary>
    public const byte ParamError = 0x01;

    /// <summary>笔未连接（仅无线转发形态）</summary>
    public const byte PenNotConnected = 0x02;

    /// <summary>笔端应答超时（终结层等 ≤1s）</summary>
    public const byte PenRespTimeout = 0x03;

    /// <summary>其他失败</summary>
    public const byte Fail = 0xFF;
}

/// <summary>
/// 统一TX §7 命令 `cmd_id`（**语义单一来源 = 统一TX 规范 §7 命令表**，规范负责人 = 笔端；
/// 本端只消费——加行/改值以规范为准，此处仅登记已用到的行）。
/// </summary>
public static class VpTxCommand
{
    /// <summary>`SET_VOICE_KEY_MODE`（K2 启动方式 1/2 下发；issues-pencil/11）</summary>
    public const byte SetVoiceKeyMode = 0x21;

    /// <summary>`QUERY_VOICE_KEY_MODE`（K2 启动方式查询；issues-pencil/11）</summary>
    public const byte QueryVoiceKeyMode = 0x42;

    /// <summary>`0x21` 载荷：`[cmd_id][mode]`（mode = 方式 1/2）</summary>
    public static byte[] BuildSetVoiceKeyMode(byte mode) => [SetVoiceKeyMode, mode];

    /// <summary>`0x42` 载荷：`[cmd_id]`（应答 `rspData` = `[mode]`）</summary>
    public static byte[] BuildQueryVoiceKeyMode() => [QueryVoiceKeyMode];
}

/// <summary>
/// 升级会话目标（公司 §3.2：固件类型；`0x07` 回显、`0x0A` 携带，须与会话目标一致 —— E23）。
/// 协议枚举故置于本命名空间（依赖方向：Fsm → Protocol）。
/// </summary>
public enum Downtype : byte
{
    /// <summary>基站固件（丙 · USB 自升级轨）</summary>
    BaseStation = 0x00,

    /// <summary>遥控笔固件（乙 · 无线轨 / 甲 · 工厂串口轨）</summary>
    Pen = 0x01
}

/// <summary>
/// `0x51` 一组配对信息：`NO + BT_MAC(6, 大端显示序) + BT_NAME(32, ASCII, 0 结束)`。
/// PC 侧**零转换**：对外一律显示序（冻结基线 R9）。
/// </summary>
public sealed record PairInfo(byte No, byte[] MacDisplay6, string BtName);

/// <summary>
/// `0x53` 应答/推送的 `STATUS`（补充 05 §3；与 `0x54` 信封 STATUS 表同款语义，一套解析覆盖两命令）。
/// </summary>
public static class PenStatusStatus
{
    /// <summary>正常（含基站槽缓存值）</summary>
    public const byte Ok = 0x00;

    /// <summary>笔未连接（断连推送与拉取应答同款，无槽区）</summary>
    public const byte PenNotConnected = 0x02;
}

/// <summary>`0x51 PARAM=03` 清绑定应答的 `STATUS`（补充 01 §4）</summary>
public static class PairClearStatus
{
    /// <summary>受理成功（先应答后动作、幂等——未绑定也回 00）</summary>
    public const byte Ok = 0x00;

    /// <summary>升级会话进行中，拒绝（会话结束重发即可）</summary>
    public const byte SessionBusy = 0xFF;
}

/// <summary>统一TX 状态槽 `state_id`（V1.2 §15.2；未知 id 前向兼容原样透传）</summary>
public static class PenStateSlotId
{
    /// <summary>`DEV_STATE`：笔端设备状态（0=正常 / 1=休眠，升级等后续扩展）</summary>
    public const byte DevState = 0x01;

    /// <summary>`BATT`：电池电量（0-100，%）</summary>
    public const byte Batt = 0x02;
}

/// <summary>`DEV_STATE` 取值（统一TX §15.2 专业叫法；PC 显示即 正常/休眠）</summary>
public static class PenDevState
{
    /// <summary>正常</summary>
    public const byte Normal = 0x00;

    /// <summary>休眠</summary>
    public const byte Sleep = 0x01;
}

/// <summary>状态槽一条：`[state_id][value]`（mini-TLV，自描述）</summary>
public sealed record PenStatusSlot(byte Id, byte Value);

/// <summary>
/// `0x53` 状态快照：`STATUS + BYTE2 副本（mode|src）+ 状态槽区 TLV 原样`。
/// PC 只搬运不解析 `state_id` 语义——未知槽前向兼容（收进 <see cref="Slots"/> 不丢弃）。
/// </summary>
public sealed record PenStatusSnapshot(byte Status, byte Byte2, IReadOnlyList<PenStatusSlot> Slots)
{
    /// <summary>统一TX byte2 bit1-0：工作模式</summary>
    public byte Mode => (byte)(Byte2 & 0x03);

    /// <summary>统一TX byte2 bit2：状态源（0=软件态 / 1=硬件态三档开关）</summary>
    public byte Src => (byte)((Byte2 >> 2) & 0x01);

    /// <summary>按 id 取槽（无则 null）</summary>
    public PenStatusSlot? Find(byte id) => Slots.FirstOrDefault(s => s.Id == id);
}

/// <summary>`0x53` 快照的展示文本（mode/src/槽语义；未知 id 前向兼容）。</summary>
public static class PenStatusText
{
    public static string ModeText(byte mode) => mode switch
    {
        0 => "待机(IDLE)",
        1 => "识别(CAPTURING)",
        2 => "扩音(AMPLIFYING)",
        _ => $"0x{mode:X2}"
    };

    public static string SrcText(byte src) => src switch
    {
        0 => "软件态",
        1 => "硬件态(三档开关)",
        _ => $"0x{src:X2}"
    };

    public static string SlotText(PenStatusSlot slot) => slot.Id switch
    {
        PenStateSlotId.DevState => slot.Value switch
        {
            PenDevState.Normal => "DEV_STATE=正常",
            PenDevState.Sleep => "DEV_STATE=休眠",
            _ => $"DEV_STATE=0x{slot.Value:X2}（未知，前向兼容）"
        },
        PenStateSlotId.Batt => $"BATT={slot.Value}%",
        _ => $"槽 0x{slot.Id:X2}=0x{slot.Value:X2}（未知 id，原样透传）"
    };
}

/// <summary>`0x03` 读型号与版本的应答字段</summary>
public sealed record VersionInfo(byte Status, byte Model, byte SverMajor, byte SverMinor, byte SverPatch);

/// <summary>`0x61` 内部调参一条读值：参数 ID + float32 当前/生效值</summary>
public sealed record TuningParamReading(byte Id, float Value);
