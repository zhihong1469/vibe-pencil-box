namespace PencilBox.Core.Protocol.Company;

/// <summary>
/// 公司 V0.9 接入的可调项（未定值集中一处，便于公司定稿后**单点切换**）。
///
/// 超时/重发默认值来源 = **骨架 §5 / 冻结基线 §4.4**（联审 L-09：三端各自宏控、可动态调，
/// 建议 开始 500ms / 块级 ≥100ms / 重发 ≤3，联调时一并校准）。
/// </summary>
public sealed class CompanyConfig
{
    /// <summary>USB 识别（弱过滤；唯一性来自 0xFF02 主特征）</summary>
    public ushort VendorId { get; init; } = ProtocolConstants.TargetVendorId;

    /// <summary>USB 识别（弱过滤）</summary>
    public ushort ProductId { get; init; } = ProtocolConstants.TargetProductId;

    /// <summary>基站板级类型序号原值（冻结基线 R10；基站实测接受 `0x01`）</summary>
    public byte HverHid { get; init; } = 0x01;

    /// <summary>
    /// 笔端板级序号原值 = **`0x06`**（R1 **V1.0** 板 `VP_BOARD_TYPE=6`；笔端需求 `06 §T7-0`：
    /// "`HVER` 必须 = 6，不符 → `DCMD=0xFF` + 原因 `0x01`"）。
    /// ⚠️ 曾误置 `0x05`（旧板），会导致**所有笔端升级被拒**。
    /// </summary>
    public byte HverPen { get; init; } = 0x06;

    /// <summary>请求-应答超时（毫秒；骨架 §5 建议 500）</summary>
    public int ResponseTimeoutMs { get; init; } = 500;

    /// <summary>超时重发次数上限（骨架 §5 建议 ≤3）</summary>
    public int MaxRetry { get; init; } = 3;

    /// <summary>`0x52` 收回 `01` 后等待 `02/03` 的窗口（毫秒；用户动作型，对齐正文 02 §⑤ 约 60s）</summary>
    public int PairingWindowMs { get; init; } = 60_000;

    /// <summary>串口形态的帧间隔/超时断帧阈值（毫秒；超时未收满即弃帧重同步）</summary>
    public int SerialFrameGapMs { get; init; } = 100;

    /// <summary>串口波特率（冻结基线 §4.1：`115200,N,8,1`）</summary>
    public int SerialBaudRate { get; init; } = 115200;

    // ===== 0x16 拉模式 OTA 时序（来源＝骨架 §5 / 冻结基线 §4.4；联审 L-09：联调时三端一并校准）=====

    /// <summary>开始帧等待应答超时（毫秒；骨架 §5 建议 500）</summary>
    public int StartTimeoutMs { get; init; } = 500;

    /// <summary>
    /// 块级等待超时（毫秒）＝ **`1500`（B1 冻结权威定值，issues/08 PC-1 / issues/05 §1-B）**，
    /// **锚点 = 本块首片发出**（非"末片发完"，二者差 ~80ms，issues/08 PC-2）。
    ///
    /// 理由链（勿改小）：
    /// ① 必须覆盖**笔端最坏重查窗 ≈800ms**（片间 200ms ×≤3 次重查，issues/07 §5.3）——
    ///    若 PC 在 400~800ms 窗口判失败，会与仍在正常重查的设备**状态分叉**；
    /// ② 覆盖 BLE 1 包/事件下 22 片传输窗（~185ms）+ 余量（≈515ms）；
    /// ③ 原 `150ms` 必误判（混同"PC 等设备处理完下一块"与"设备侧片计数超时"两个不同的量——
    ///    片间隔由 <see cref="ChunkGapMs"/> 保证，与本值无关）。
    /// 已由 USB 真升级 224 块全序实证（box/test 07 §6.3）。收到 `0x0A` 即立刻继续，对快设备无额外延迟。
    /// </summary>
    public int BlockTimeoutMs { get; init; } = 1500;

    /// <summary>数据片发送间隔（毫秒；公司 §3.5 工程注：≥5ms 由 PC 产生）</summary>
    public int ChunkGapMs { get; init; } = 5;

    /// <summary>OTA 超时重试上限（骨架 §5 建议 ≤3）</summary>
    public int OtaMaxRetry { get; init; } = 3;

    // ===== 0x54 统一TX 转发信封（issues-pencil/12 §2.5；pc-issues/03 P-3 备案值）=====

    /// <summary>
    /// 信封等待超时——**无线形态**（基站终结，等笔端 RSP ≤1s）必须 **> 基站 1s + 裕量**
    /// （风格同 <see cref="BlockTimeoutMs"/>"大于下游最坏窗"；串口直连用 <see cref="EnvelopeTimeoutSerialMs"/>）。
    /// </summary>
    public int EnvelopeTimeoutMs { get; init; } = 2000;

    /// <summary>信封等待超时——**串口直连形态**（笔端本地终结，无转发跳；12 号 §2.6 串口先行）</summary>
    public int EnvelopeTimeoutSerialMs { get; init; } = 1000;

    /// <summary>信封重发上限（R4：重试归 **PC**，SET 幂等 / QUERY 无副作用；基站不缓存不重试）</summary>
    public int EnvelopeMaxRetry { get; init; } = 3;

    /// <summary>
    /// 固件镜像长度上限（字节）= **`0x32000`（204800）**。
    /// 笔端约束（`06 §T7-0`）：`FILELEN` 超此值 → `DCMD=0xFF` + 原因 `0x02`（空间不足）。
    /// </summary>
    public int MaxImageBytes { get; init; } = 0x32000;

    /// <summary>
    /// **G14 冻结（issues/05 §1-D）**：无线轨（B1 透传）`0x03` 读**笔端**版本时，
    /// 请求第 4 字节 `target` 须显式填**笔端类别**。
    /// MODEL 位段 = 类别(bit7-4) + 序号(bit3-0)：`0x10` = R1/CGC V0.9、**`0x11` = R1 V1.0（当前板，
    /// 与 <see cref="HverPen"/>=6 对应）**；笔端按 `0x1x` 类别段应答（issues/07 §5.1③，两值皆通）。
    /// ⚠️ **串口轨恒 `0x00` 不变**（既有黄金向量不动）；读基站也填 `0x00`（基站自答）。
    /// </summary>
    public byte PenTargetClass { get; init; } = 0x11;
}
