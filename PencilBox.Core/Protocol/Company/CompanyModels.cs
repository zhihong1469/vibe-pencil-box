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

    /// <summary>
    /// 读 Git Hash 内部指令（定稿 2026-10-16：`_share/issues/0-待处理/产品落地/版本管理/Box/读hash内部指令-0x04协议定稿与上位机对齐（最终版）.md`）。
    /// `target` 复用 0x03 语义：`0x00` 基站（USB 轨）/ `0x11` 笔端（**仅插线 UART1 串口直达**，
    /// 无线轨不开放——40B 载荷占 BLE 带宽与音频相冲，定案 §8-1）。应答 `STATUS + 40B hex ASCII`。
    /// </summary>
    public const byte ReadHash = 0x04;

    /// <summary>配对信息读写（`PARAM` 01 读 / 02 写 / 03 清绑定——补充 01，仅笔端串口轨）</summary>
    public const byte PairInfo = 0x51;

    /// <summary>进入配对模式（基站实现；笔端不做）</summary>
    public const byte PairEnter = 0x52;

    /// <summary>
    /// 快捷键可配置 `0x55`（pc-issues/05 §2 **帧面草案**——三端未裁决、基站未实现；
    /// PC 发送=探测先行，无应答按"基站未响应"降级为仅上位机生效，见 CompanyKeymapConfigService）。
    /// PARAM 01 读条目/元信息 · 02 写条目(RAM) · 03 提交(NVDS) · 05/06 切换表读/写（mode=0 内部行拒写）。
    /// </summary>
    public const byte KeymapConfig = 0x55;

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

    /// <summary>
    /// 空鼠遥测流（笔端 06 卷：`0x62/01` START · `0x62/00` STOP · LEN=21 载荷=流帧）。
    /// 与 <see cref="TuningParam"/> 同族——**仅串口轨**、`srv_f5_cmds` 就地应答不进 FSM、
    /// 流帧无应答 PC 不回帧。数值与 <see cref="CompanyFrame.BaseCmdRequest"/> 的 `0x62` 重叠
    /// 同属 CMDTYPE 命名空间巧合。
    /// </summary>
    public const byte StreamTelemetry = 0x62;
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

/// <summary>`0x53` 状态槽 `state_id`（mini-TLV；未知 id 前向兼容原样透传）</summary>
public static class PenStateSlotId
{
    /// <summary>`DEV_STATE`：笔端设备状态（0=正常 / 1=休眠，升级等后续扩展；V1.3 值域挂起恒 0）</summary>
    public const byte DevState = 0x01;

    /// <summary>`BATT`：电池电量（0-100，%）</summary>
    public const byte Batt = 0x02;

    /// <summary>
    /// `RSSI`：0x53 通道自有槽（2026-10-18 采纳，协议补充/05 §9）——基站本地测量的对笔 RSSI，
    /// value=1B dBm 补码原值（-60dBm→0xC4）。⚠️ 与统一TX 侧历史空鼠槽 0x03 字面撞号、无继承关系；
    /// 旧基站固件无此槽（TLV 动态退化），消费方以"有槽才更新"兜底。
    /// </summary>
    public const byte Rssi = 0x03;
}

/// <summary>`DEV_STATE` 取值（统一TX §15.2 专业叫法；PC 显示即 正常/休眠）</summary>
public static class PenDevState
{
    /// <summary>正常</summary>
    public const byte Normal = 0x00;

    /// <summary>休眠</summary>
    public const byte Sleep = 0x01;
}

/// <summary>
/// 统一TX byte2 bit1-0 充电状态枚举（V1.3 定版：byte2 语义从"工作模式/档位标签"腾挪为充电枚举，
/// 0=未充电/1=充电中/2=充满/3=保留；bit2=src 维持硬件态；与笔端 `app_batt.c` chg 日志值域同构）。
/// 工作模式上报已退役：识别启停权威=0x13→F13/F14、扩音档权威=0x16→F15/F16，上位机凭事件自行推导。
/// </summary>
public static class PenChargeState
{
    /// <summary>未充电</summary>
    public const byte None = 0x00;

    /// <summary>充电中</summary>
    public const byte Charging = 0x01;

    /// <summary>已充满（插着，FINISH 真判据+软件锁存）</summary>
    public const byte Full = 0x02;

    /// <summary>保留</summary>
    public const byte Reserved = 0x03;
}

/// <summary>状态槽一条：`[state_id][value]`（mini-TLV，自描述）</summary>
public sealed record PenStatusSlot(byte Id, byte Value);

/// <summary>
/// `0x53` 状态快照：`STATUS + BYTE2 副本（充电枚举|src，V1.3）+ 状态槽区 TLV 原样`。
/// PC 只搬运不解析 `state_id` 语义——未知槽前向兼容（收进 <see cref="Slots"/> 不丢弃）。
/// </summary>
public sealed record PenStatusSnapshot(byte Status, byte Byte2, IReadOnlyList<PenStatusSlot> Slots)
{
    /// <summary>统一TX byte2 bit1-0：充电状态枚举（V1.3，<see cref="PenChargeState"/>）</summary>
    public byte ChargeState => (byte)(Byte2 & 0x03);

    /// <summary>统一TX byte2 bit2：状态源（0=软件态 / 1=硬件态三档开关）</summary>
    public byte Src => (byte)((Byte2 >> 2) & 0x01);

    /// <summary>按 id 取槽（无则 null）</summary>
    public PenStatusSlot? Find(byte id) => Slots.FirstOrDefault(s => s.Id == id);

    /// <summary>0x03 槽 RSSI（dBm 负值；无槽=null——旧基站固件/连接后首次检查前，消费方保持原值）</summary>
    public sbyte? RssiDbm => Find(PenStateSlotId.Rssi) is { } slot ? (sbyte)slot.Value : null;
}

/// <summary>`0x53` 快照的展示文本（充电枚举/src/槽语义；未知 id 前向兼容）。</summary>
public static class PenStatusText
{
    public static string ChargeStateText(byte chargeState) => chargeState switch
    {
        PenChargeState.None => "未充电",
        PenChargeState.Charging => "充电中",
        PenChargeState.Full => "已充满",
        _ => $"0x{chargeState:X2}"
    };

    public static string SrcText(byte src) => src switch
    {
        0 => "软件态",
        1 => "硬件态(三档开关)",
        _ => $"0x{src:X2}"
    };

    public static string SlotText(PenStatusSlot slot, byte chargeState = PenChargeState.None) => slot.Id switch
    {
        PenStateSlotId.DevState => slot.Value switch
        {
            PenDevState.Normal => "DEV_STATE=正常",
            PenDevState.Sleep => "DEV_STATE=休眠",
            _ => $"DEV_STATE=0x{slot.Value:X2}（未知，前向兼容）"
        },
        // 充电中读数虚高（CC 段 I×R 叠加，电池 05 号 K1）：数值照出+文字标注不可靠（灰显语义的 CLI 形态，指挥官 10-18 定版）；
        // 已充满不标注（FINISH 真判据下读数近真值，拔出不跳）
        PenStateSlotId.Batt => chargeState == PenChargeState.Charging
            ? $"BATT={slot.Value}%（充电中·电压虚高，读数仅供参考）"
            : $"BATT={slot.Value}%",
        PenStateSlotId.Rssi => $"RSSI={(sbyte)slot.Value}dBm",
        _ => $"槽 0x{slot.Id:X2}=0x{slot.Value:X2}（未知 id，原样透传）"
    };
}

/// <summary>`0x03` 读型号与版本的应答字段</summary>
public sealed record VersionInfo(byte Status, byte Model, byte SverMajor, byte SverMinor, byte SverPatch);

/// <summary>`0x61` 内部调参一条读值：参数 ID + float32 当前/生效值</summary>
public sealed record TuningParamReading(byte Id, float Value);

/// <summary>`0x62` 子命令 `CMDTYPE`（笔端 06 卷 §1：01=START / 00=STOP）</summary>
public static class StreamCmd
{
    /// <summary>START：请求载荷 `[divider u8]`（1=每帧 50Hz、2=25Hz…）；应答 `[01][STATUS][echo divider]`</summary>
    public const byte Start = 0x01;

    /// <summary>STOP：请求空载荷；应答 `[00][STATUS]`；未开再停=00 幂等</summary>
    public const byte Stop = 0x00;

    /// <summary>流帧载荷 v1 定长（21B；帧 LEN 字节=0x18、总长 28B）——v2 前旧口径，兼容收</summary>
    public const int FramePayloadLengthV1 = 21;

    /// <summary>流帧载荷 v2 定长（23B=v1 尾部追加 temp_imu；帧 LEN 字节=0x1A、总长 30B）——笔端 06 卷 §2 v2 权威</summary>
    public const int FramePayloadLength = 23;

    /// <summary>是流帧载荷？v2 23B 权威、v1 21B 兼容（真机已发 v2，v1 仅旧固件/旧录波）</summary>
    public static bool IsFramePayload(int payloadLength) =>
        payloadLength is FramePayloadLengthV1 or FramePayloadLength;

    /// <summary>应答与流帧的判别口径：CMDTYPE `0x62` 载荷 ≤3B=START/STOP 应答（与请求配对消费），21B/23B=流帧</summary>
    public static bool IsResponse(int payloadLength) => payloadLength is >= 2 and <= 3;
}

/// <summary>`0x62` 流帧 `flags` 位（笔端 06 卷 §2）</summary>
public static class TelemetryFlags
{
    /// <summary>bit0：冻结期（含释放尾窗）</summary>
    public const byte Frozen = 0x01;

    /// <summary>bit1：意图门已转拖拽</summary>
    public const byte DragIntent = 0x02;

    /// <summary>bit2：零偏已标定</summary>
    public const byte BiasCalibrated = 0x04;

    /// <summary>bit3：本次 boot 零偏来自 NVDS 装载（零偏机制 v2，验证回合判装载生效）</summary>
    public const byte BiasFromNvds = 0x08;
}

/// <summary>
/// `0x62` 流帧（v2 定长 23B，大端，笔端 06 卷 §2；v1 21B 兼容收、<see cref="TempImu"/> 缺省 0）：
/// raw 六轴为定点原码（陀螺 0.01dps/加速度 0.001g，S10 笔端定案，不换算不去偏，PC 全链路直读物理量）；
/// rate 为滤波后 0.01dps 定点；out 为 HID 出口位移；temp_imu 为 0.01°C 定点（笔端 1Hz 刷新、其余帧重复上值）。
/// </summary>
public sealed record TelemetryFrame(
    ushort Seq,
    byte Flags,
    short RawGx, short RawGy, short RawGz,
    short RawAx, short RawAy, short RawAz,
    short RateYaw, short RateRoll,
    sbyte OutX, sbyte OutY,
    short TempImu = 0)
{
    /// <summary>bit0：冻结期（含尾窗）</summary>
    public bool IsFrozen => (Flags & TelemetryFlags.Frozen) != 0;

    /// <summary>bit1：意图门已转拖拽</summary>
    public bool IsDragIntent => (Flags & TelemetryFlags.DragIntent) != 0;

    /// <summary>bit2：零偏已标定</summary>
    public bool IsBiasCalibrated => (Flags & TelemetryFlags.BiasCalibrated) != 0;

    /// <summary>bit3：本次 boot 零偏来自 NVDS 装载（零偏机制 v2）</summary>
    public bool IsBiasFromNvds => (Flags & TelemetryFlags.BiasFromNvds) != 0;

    /// <summary>IMU 温度（°C；= TempImu×0.01，定点单点换算在此一处）</summary>
    public double TempC => TempImu * 0.01;
}
