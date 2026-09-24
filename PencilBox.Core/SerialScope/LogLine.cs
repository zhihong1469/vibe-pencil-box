namespace PencilBox.Core.SerialScope;

/// <summary>统一时间轴上的一行的来源：设备**文本日志** / F5 **协议帧**</summary>
public enum LogSource
{
    /// <summary>设备打印的文本日志行（按换行符切行后）</summary>
    Text,

    /// <summary>F5 逻辑帧（已定帧、未补零；<see cref="LogLine.Raw"/> 为原始 HEX）</summary>
    Frame
}

/// <summary>
/// 统一时间轴上的一行 —— **人与 AI 共读的最小单位**（05 文档 §3.2）。
///
/// 时间戳一律取**同一主机时钟、毫秒级**：跨端口（笔端 / 基站）可按时间排序，
/// 这是"双口对拍"能成立的前提（05 §2 / §3.3）。
/// </summary>
/// <param name="Timestamp">收行时刻（本机时钟，毫秒级）</param>
/// <param name="PortName">端口标签（配置里的 `--name`，如 `DONGLE` / `PENCIL`；未配置则为 `COMx`）</param>
/// <param name="Source">文本日志行 / F5 协议帧</param>
/// <param name="Domain">域标签，见 <see cref="LogDomain"/>（文本行由分域器判定；帧恒为 `f5`）</param>
/// <param name="Raw">原始内容：文本行为原行；帧为完整逻辑帧 HEX（无分隔）</param>
/// <param name="Decoded">面向人的呈现：文本行为清理 ANSI 后的原行；帧为解码文本（如 `↓0x16 数据块 packh=12/224`）</param>
public sealed record LogLine(
    DateTimeOffset Timestamp,
    string PortName,
    LogSource Source,
    string Domain,
    string Raw,
    string Decoded);

/// <summary>
/// 设备日志的**域标签**（05 §1.1 / §3.3）。
/// 取值一律小写，便于 `grep`/过滤大小写无关地命中；设备原件里的大写形态（`[GATT_CLIENT]`）也归一到这里。
/// </summary>
public static class LogDomain
{
    /// <summary>升级内部（设备侧 `dual_bank_*` / 块进度 / 终态）</summary>
    public const string OtaUsb = "ota_usb";

    /// <summary>F5 公司协议（`rx cmdtype=…` 等服务端协议日志；本工具解出的帧也归此域）</summary>
    public const string F5 = "f5";

    /// <summary>配对</summary>
    public const string Pair = "pair";

    /// <summary>BLE 扫描（设备侧 `[GATT_CLIENT]`）</summary>
    public const string GattClient = "gatt_client";

    /// <summary>蓝牙栈</summary>
    public const string Ble = "ble";

    /// <summary>USB 栈</summary>
    public const string Usb = "usb";

    /// <summary>未接实现的占位（`[TODO]` / `[STUB]`）—— 只为让人**一眼定位"哪儿还没接"**（坑点 §八）</summary>
    public const string Todo = "todo";

    /// <summary>本工具自身产生的行（会话标记、错误提示）</summary>
    public const string Sys = "sys";

    /// <summary>其它：JTAG 二进制片段（`36 8B D7 AC…` / `^`）、无法归类者（05 §5.4，默认折叠）</summary>
    public const string Other = "other";
}
