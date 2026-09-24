using PencilBox.Core.Communication;

namespace PencilBox.Core.Ble;

/// <summary>
/// 笔端 BLE 广播识别与推荐逻辑（纯逻辑，无平台依赖，可单测）。
/// 识别特征：广播名以 <see cref="BleNameConstants.PenPrefix"/> 开头（SunVote 笔端）；
/// 配对态（名字=PairName）在推荐时优先；同态按 RSSI 信号强度排序。
/// </summary>
public static class BleNaming
{
    /// <summary>是否 SunVote 笔端广播（按名字前缀识别）</summary>
    public static bool IsSunVotePen(string? localName) =>
        !string.IsNullOrEmpty(localName)
        && localName.StartsWith(BleNameConstants.PenPrefix, StringComparison.Ordinal);

    /// <summary>是否配对态广播（名字以 '#' 结尾，= PairName）</summary>
    public static bool IsPairingMode(string? localName) =>
        !string.IsNullOrEmpty(localName)
        && localName.EndsWith(BleNameConstants.PairName, StringComparison.Ordinal);

    /// <summary>
    /// 从常态名解析内嵌的 MAC HEX 尾号（**仅作展示/辅助识别；完整 MAC 以广播地址为准**）。
    /// 兼容两种宽度：短尾号 6 HEX（末 3 字节，旧口径）/ 全尾号 12 HEX（全 6 字节，新口径）。
    /// 例：`SunVote-R1:028478` → `028478`；`SunVote-R1:C656003A5DF1` → `C656003A5DF1`；
    /// 配对名/无分隔符/宽度不合法 → null。
    /// </summary>
    public static string? TryGetMacSuffix(string? localName)
    {
        if (string.IsNullOrEmpty(localName))
            return null;

        int sep = localName.IndexOf(BleNameConstants.NormSeparator);
        if (sep < 0 || sep + 1 >= localName.Length)
            return null;

        var suffix = localName.Substring(sep + 1);
        return suffix.Length is BleNameConstants.NormMacSuffixLength or BleNameConstants.NormMacSuffixFullLength
            ? suffix
            : null;
    }

    /// <summary>
    /// 从常态名解析**完整显示序 MAC**（仅当名字带全尾号 12 HEX 时可用；否则返回 null）。
    /// 用途：与广播地址交叉校验，或名字是唯一来源时兜底（大小端一律经 <see cref="MacAddress"/>）。
    /// </summary>
    public static byte[]? TryGetFullMacFromName(string? localName)
    {
        var suffix = TryGetMacSuffix(localName);
        if (suffix == null || suffix.Length != BleNameConstants.NormMacSuffixFullLength)
            return null;

        try
        {
            return MacAddress.ParseDisplay(suffix);
        }
        catch (Exception)
        {
            return null;   // 非 HEX 字符等：交给调用方按"名字不可信"处理
        }
    }

    /// <summary>
    /// 名字尾号是否与权威 MAC（广播地址，显示序）一致；名字无尾号时返回 null（无法判定）。
    /// 用于"名字只作展示"的对齐校验：不一致时以广播地址为准并告警。
    /// </summary>
    public static bool? IsMacSuffixConsistent(string? localName, ReadOnlySpan<byte> macDisplay6)
    {
        var suffix = TryGetMacSuffix(localName);
        if (suffix == null)
            return null;

        var hex = Convert.ToHexString(macDisplay6);   // 显示序 12 HEX（大写）
        return suffix.Length switch
        {
            BleNameConstants.NormMacSuffixFullLength => suffix.Equals(hex, StringComparison.OrdinalIgnoreCase),
            BleNameConstants.NormMacSuffixLength => suffix.Equals(hex[^6..], StringComparison.OrdinalIgnoreCase),
            _ => null
        };
    }

    /// <summary>
    /// 按协议口径拼出 `BT_NAME`（32B 字段的字符串形态）：`"SunVote-R1:" + 12 位大写 HEX(显示序 MAC)`。
    /// **PC 自行拼接**（笔端 08 决议 D6；与笔端 `0x51` 读回值逐字节一致，实测 `SunVote-R1:C656003A5DF1`）。
    /// </summary>
    public static string BuildBtName(ReadOnlySpan<byte> macDisplay6) =>
        BleNameConstants.PenPrefix + BleNameConstants.NormSeparator + Convert.ToHexString(macDisplay6);

    /// <summary>
    /// 推荐目标：配对态广播绝对优先；否则（常态）按 RSSI 从强到弱取第一条。
    /// 仅返回 SunVote 笔端。用于上位机自动挑一个目标 MAC。
    /// </summary>
    public static BleDeviceInfo? Recommend(IEnumerable<BleDeviceInfo> devices)
    {
        var pens = devices.Where(d => IsSunVotePen(d.LocalName)).ToList();
        if (pens.Count == 0)
            return null;

        // 配对态优先于常态；同态按 RSSI 降序（信号强=更近，更可能是操作目标）
        return pens
            .OrderByDescending(d => IsPairingMode(d.LocalName))
            .ThenByDescending(d => d.Rssi)
            .FirstOrDefault();
    }
}
