namespace PencilBox.Core.Ble;

/// <summary>
/// 笔端 BLE 广播命名/识别常量（单源，对齐 pair/issues/1-执行中/now/双配对方案/小批量50套双模式配对方案.md）。
///
/// 命名规则：
/// - 常态名：`SunVote-R1:<MAC HEX>`——名字仅作可见标识，**不承担连接**（连接走 INFO3 MAC / `0x51` 写）。
///   **两种宽度并存**：短尾号 6 HEX（MAC 末 3 字节，如 `SunVote-R1:028478`）、
///   全尾号 12 HEX（MAC 全 6 字节，如 `SunVote-R1:C656003A5DF1`，笔端 08 决议 D6 后的新口径）。
/// - 配对名：`SunVote-R1#`——Dongle 名称配对只认此名（11B 全长比较），不会误抓常态广播。
/// - 关键解耦：配对名以 `#` 结尾、常态名以 `:` 分隔。
/// - **权威 MAC 一律取广播地址**（<see cref="Communication.BleDeviceInfo.MacDisplayOrder"/>）；
///   名字里的尾号只作展示/交叉校验——它是"给人看的"，PC 可自行拼接（大小端一律经 <see cref="MacAddress"/>）。
/// </summary>
public static class BleNameConstants
{
    /// <summary>笔端广播名前缀（常态/配对共用）</summary>
    public const string PenPrefix = "SunVote-R1";

    /// <summary>配对名（Dongle 学习模式匹配目标），以 '#' 结尾</summary>
    public const string PairName = "SunVote-R1#";

    /// <summary>常态名分隔符（后面跟 MAC HEX 尾号）</summary>
    public const char NormSeparator = ':';

    /// <summary>配对名尾标记</summary>
    public const char PairSuffix = '#';

    /// <summary>常态名内嵌的**短**尾号长度：MAC 末 3 字节（6 HEX，旧口径）</summary>
    public const int NormMacSuffixLength = 6;

    /// <summary>常态名内嵌的**全**尾号长度：MAC 全 6 字节（12 HEX，新口径）</summary>
    public const int NormMacSuffixFullLength = 12;

    /// <summary>协议 `BT_NAME` 字段宽度（骨架 §6.1：32B ASCII，0 结束）</summary>
    public const int BtNameFieldLength = 32;
}
