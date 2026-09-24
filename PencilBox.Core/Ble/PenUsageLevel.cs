namespace PencilBox.Core.Ble;

/// <summary>
/// 已知笔端的使用层级（下拉框三级过滤降序：LastUsed &gt; Used &gt; Seen）。
/// 数值仅用于排序（数值大=更优先），非协议值。
/// </summary>
public enum PenUsageLevel
{
    /// <summary>0: 出现过（仅扫描到/历史保存，从未用于绑定下发）</summary>
    Seen = 1,

    /// <summary>1: 使用过（至少绑定制发过一次，非最近一次）</summary>
    Used = 2,

    /// <summary>2: 上次使用（最近一次绑定下发的设备）</summary>
    LastUsed = 3
}

public static class PenUsageLevelExtensions
{
    /// <summary>中文显示名（UI 徽标）</summary>
    public static string ToDisplayName(this PenUsageLevel level) => level switch
    {
        PenUsageLevel.LastUsed => "上次使用",
        PenUsageLevel.Used => "使用过",
        _ => "出现过"
    };
}
