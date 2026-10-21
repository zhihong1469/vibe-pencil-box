// <copyright file="HidDeviceSelection.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Protocol;

namespace PencilBox.Core.Communication;

/// <summary>
/// 多实例扫描差异化选择（多支设备同升 02 号的上位机前提）。
///
/// 同 VID/PID 的多支 dongle 同时插入时，Windows 按序列号各建独立实例
/// （`USB\VID_2F70&amp;PID_EA11\&lt;序列号&gt;`），设备路径内嵌该实例 ID——
/// 个体区分靠序列号，无需新协议。本选择器把"插了几台、选哪台"变成**显式结果**：
/// **禁止静默取第一台**（0 台报错 / 1 台自动 / N 台未指定时列名单要求选择）。
/// CLI 走 `--serial`，Desktop 走下拉框，裁剪小工具走设备列表逐支行。
/// </summary>
public static class HidDeviceSelection
{
    /// <summary>
    /// 从全量 HID 枚举里筛出**本产品命令通道实例**（每支一台）。口径与既有 EnsureUsbAsync 双层一致：
    /// 首选 0xFF02 无 RID 命令通道（IsCommandChannel）；零命中时兜底 legacy 0xA0/0x05（IsTargetDongle）。
    /// </summary>
    public static List<HidDeviceInfo> CommandChannelInstances(IEnumerable<HidDeviceInfo> devices)
    {
        var list = devices as IReadOnlyList<HidDeviceInfo> ?? devices.ToList();
        var cmd = list.Where(d => d.IsCommandChannel && IsTarget(d)).ToList();
        if (cmd.Count == 0)
            cmd = list.Where(d => d.IsTargetDongle && IsTarget(d)).ToList();
        return cmd;
    }

    /// <summary>
    /// 按 <paramref name="token"/> 选一台（序列号或路径片段）。token 为空时：唯一实例自动选中、
    /// 多实例返回候选名单错误；非空时先序列号精确/唯一前缀，命中多台（**同号设备**，实测存在：
    /// 2026-10-21 双台同报 CD7113…）再退路径片段唯一子串匹配。
    /// </summary>
    public static HidDeviceSelectionResult Select(IReadOnlyList<HidDeviceInfo> devices, string? token)
    {
        var candidates = CommandChannelInstances(devices);

        if (candidates.Count == 0)
            return new HidDeviceSelectionResult(
                Array.Empty<HidDeviceInfo>(), null,
                "未找到本产品 EP4 命令通道设备（0xFF02；VID 0x2F70/0xEA11）——确认接收器已插入且此电脑能识别");

        if (string.IsNullOrWhiteSpace(token))
        {
            if (candidates.Count == 1)
                return new HidDeviceSelectionResult(candidates, candidates[0], null);

            return new HidDeviceSelectionResult(candidates, null,
                $"发现 {candidates.Count} 台命令通道设备（{DescribeList(candidates)}）——" +
                "请用 --serial <序列号或路径片段> 选择目标（界面在设备下拉框选择）");
        }

        var want = token.Trim();

        var serialHits = candidates
            .Where(d => string.Equals(d.SerialNumber, want, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (serialHits.Count == 0)
            serialHits = candidates
                .Where(d => d.SerialNumber.StartsWith(want, StringComparison.OrdinalIgnoreCase))
                .ToList();

        if (serialHits.Count == 1)
            return new HidDeviceSelectionResult(candidates, serialHits[0], null);

        // 序列号撞号（>1 命中或 0 命中）→ 路径片段唯一子串匹配（多实例区分退化到端口号，02 号观察项实证）
        if (serialHits.Count != 1)
        {
            var pathHits = candidates
                .Where(d => d.DevicePath.Contains(want, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (pathHits.Count == 1)
                return new HidDeviceSelectionResult(candidates, pathHits[0], null);

            return new HidDeviceSelectionResult(candidates, null,
                serialHits.Count > 1
                    ? $"序列号 \"{want}\" 命中 {serialHits.Count} 台（同号设备）——请用路径片段选择：{DescribeList(serialHits)}"
                    : pathHits.Count > 1
                        ? $"路径片段 \"{want}\" 命中 {pathHits.Count} 台——请输更长片段：{DescribeList(pathHits)}"
                        : $"无序列号或路径以 \"{want}\" 命中的命令通道设备；在插设备：{DescribeList(candidates)}");
        }

        return new HidDeviceSelectionResult(candidates, null, "设备选择失败");
    }

    /// <summary>
    /// 路径定位片段：HID 实例路径中 `#X&amp;Y&amp;Z#{GUID}` 的 `X&amp;Y&amp;Z` 段（USB 母线/端口号）——
    /// 同号多台时是最后的个体判据；解析不出回退 "(位置未知)"。注意含 `&amp;`，命令行使用须加引号。
    /// </summary>
    public static string PathToken(HidDeviceInfo device)
    {
        var path = device.DevicePath;
        var hash = path.IndexOf('#', path.IndexOf('#') + 1);   // 第二个 # 之后 = 实例段
        if (hash >= 0 && hash + 1 < path.Length)
        {
            var tail = path[(hash + 1)..];
            var end = tail.IndexOf('#');
            var seg = end > 0 ? tail[..end] : tail;
            if (seg.Length > 0 && seg != device.SerialNumber)
                return seg;
        }
        return "(位置未知)";
    }

    /// <summary>
    /// 设备体系分组键：位置片段去掉尾部集合序号（`…&amp;0&amp;0000` → `…&amp;0`）。
    /// 同一支实体设备的各接口/集合（命令通道、键盘、HID 输入集合…）共享同一实例主体，
    /// 按此键归组 = **按设备分类而非按通道分类**（一个设备 ID 挂它名下的全部通道）。
    /// 序列号撞号（同号双台）时序列号不可作分组键——USB 实例主体才是物理事实。
    /// </summary>
    public static string DeviceGroupKey(HidDeviceInfo device)
    {
        var seg = PathToken(device);
        if (seg == "(位置未知)")
            return device.DevicePath;   // 兜底：整路径也互异，至少不串台
        var cut = seg.LastIndexOf('&');
        if (cut > 0 && seg[(cut + 1)..].All(char.IsDigit))
            seg = seg[..cut];
        return seg;
    }

    /// <summary>
    /// 按设备体系归组：以命令通道为锚（每支一台），组内挂同一实体设备的全部通道。
    /// 产物：扫描列表一行=一台设备（多支各占一行），展开可见其名下通道——
    /// 替代"全部命令通道平铺一列"的同貌列表（2026-10-21 指挥官截图问题）。
    /// </summary>
    public static List<HidDeviceGroup> DeviceGroups(IEnumerable<HidDeviceInfo> devices)
    {
        var list = devices as IReadOnlyList<HidDeviceInfo> ?? devices.ToList();
        var anchors = CommandChannelInstances(list);
        var groups = new List<HidDeviceGroup>(anchors.Count);
        foreach (var anchor in anchors)
        {
            var key = DeviceGroupKey(anchor);
            var channels = list.Where(d => DeviceGroupKey(d) == key).ToList();
            groups.Add(new HidDeviceGroup(key, anchor, channels));
        }
        return groups;
    }

    /// <summary>候选项单行描述（序列号 + 路径片段，同号多台时靠后者区分）。</summary>
    public static string Describe(HidDeviceInfo d) =>
        $"SN {Show(d.SerialNumber)}";

    private static string DescribeList(IReadOnlyList<HidDeviceInfo> devices) =>
        string.Join("、", devices.Select((d, i) => $"[{i + 1}]{Describe(d)}/位置 {PathToken(d)}"));

    private static string Show(string s) => string.IsNullOrEmpty(s) ? "(无序列号)" : s;

    private static bool IsTarget(HidDeviceInfo d) => ProtocolConstants.IsTargetVidPid(d.VendorId, d.ProductId);

    private static string SerialList(IReadOnlyList<HidDeviceInfo> devices) =>
        string.Join("、", devices.Select((d, i) => $"[{i + 1}]{(string.IsNullOrEmpty(d.SerialNumber) ? "(无序列号)" : d.SerialNumber)}"));
}

/// <summary>选择结果：<see cref="Error"/> 非 null = 未选中（<see cref="Selected"/> 为 null），<see cref="Candidates"/> 供列名单/下拉框。</summary>
public sealed record HidDeviceSelectionResult(
    IReadOnlyList<HidDeviceInfo> Candidates,
    HidDeviceInfo? Selected,
    string? Error);

/// <summary>
/// 一台实体设备的归组：锚=命令通道（升级走它），<see cref="Channels"/>=该实体名下全部通道
/// （命令通道/键盘/HID 输入集合…，供界面展示"这是一个设备体系"）。
/// </summary>
public sealed record HidDeviceGroup(string Key, HidDeviceInfo CommandChannel, IReadOnlyList<HidDeviceInfo> Channels);
