// <copyright file="UsbProblemReport.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Communication;

/// <summary>
/// USB 设备层（PnP）本产品实例的一行事实：实例 ID + 配置结果码。
/// HID 枚举只看得见 Windows 成功拉起的台；"插着却不识别"的台在这层就是配置失败（Code≠0）。
/// </summary>
/// <param name="DeviceId">PnP 实例 ID，如 USB\VID_2F70&amp;PID_EA11\7&amp;1F9D7D9F&amp;0&amp;1（尾段=USB 口路径，同口必同段）。</param>
/// <param name="ProblemCode">ConfigManagerErrorCode：0=正常，10=无法启动，28=未装驱动……</param>
public sealed record UsbPnpInstance(string DeviceId, int ProblemCode);

/// <summary>
/// USB 设备层异常盘点的纯格式化（取数端=OtaCore UsbProblemProbe，net48 WMI）：
/// 6 插 4 识别问题的显性化——软件从未去重，缺口=Windows 层没拉起来的台，在此给出总数/形态/端口标识与处置指引。
/// </summary>
public static class UsbProblemReport
{
    /// <summary>复合设备的 MI 子接口（实例 ID 带 &amp;MI_，如 HID/媒体集合）非整台；台数对拍只数复合根实例。</summary>
    public static bool IsCompositeChild(string deviceId) =>
        deviceId.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// 可重置目标=异常的复合根台（Code≠0）：子接口异常不单独重置（重置的是整台），
    /// 排序异常码升序（无法启动在前）。pnputil 重启设备=软件拔插，Code=10 这类启动失败常可借此复活。
    /// </summary>
    public static IReadOnlyList<UsbPnpInstance> RestartTargets(IReadOnlyList<UsbPnpInstance> instances) =>
        instances.Where(i => !IsCompositeChild(i.DeviceId) && i.ProblemCode != 0)
            .OrderBy(i => i.ProblemCode)
            .ThenBy(i => i.DeviceId, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>配置结果码 → 中文短句（未知码原样露出，不猜）。</summary>
    public static string CodeText(int code) => code switch
    {
        0 => "正常",
        10 => "无法启动(Code10)",
        22 => "已禁用(Code22)",
        28 => "未装驱动(Code28)",
        45 => "正在连接(Code45)",
        _ => $"异常(Code{code})",
    };

    /// <summary>
    /// 扫描附带的诊断行：以"台"（复合根实例）为口径的总数/异常数/形态汇总 + 处置指引；
    /// 整台起得来但某子接口配置失败时另起一句（接口级问题拔插整台通常也能解）。
    /// 全正常时也报台数（与现场台数对拍用）。
    /// </summary>
    public static string DescribeLine(IReadOnlyList<UsbPnpInstance> instances)
    {
        var roots = instances.Where(i => !IsCompositeChild(i.DeviceId)).ToList();
        var bad = roots.Where(i => i.ProblemCode != 0).ToList();
        var badChildren = instances.Count(i => IsCompositeChild(i.DeviceId) && i.ProblemCode != 0);
        var head = $"USB 设备层：本产品 {roots.Count} 台（{bad.Count} 台异常）";
        if (instances.Count == 0)
            return head + "——设备未插入或此电脑未识别，检查线缆/USB 口供电";
        if (bad.Count == 0 && badChildren == 0)
            return head;
        var parts = new List<string>();
        if (bad.Count > 0)
        {
            parts.Add(string.Join("、", bad.GroupBy(i => i.ProblemCode)
                .OrderBy(g => g.Key)
                .Select(g => $"{CodeText(g.Key)}×{g.Count()}")));
        }
        if (badChildren > 0)
            parts.Add($"另有 {badChildren} 个接口子实例异常（接口级驱动问题）");
        return head + "：" + string.Join("；", parts) +
               "；异常台不出现在设备列表——拔下重插或换 USB 口后再刷新";
    }

    /// <summary>全量名单（CLI scan --raw）：异常台排前，每台带实例 ID（尾段=USB 口路径标识，可定位到口）。</summary>
    public static IReadOnlyList<string> RawLines(IReadOnlyList<UsbPnpInstance> instances)
    {
        var lines = new List<string>
        {
            $"USB 设备层全量（含异常台与 MI 子接口，共 {instances.Count} 条；根实例尾段=USB 口路径标识）：",
        };
        lines.AddRange(instances
            .OrderByDescending(i => i.ProblemCode != 0)
            .ThenBy(i => i.DeviceId, StringComparer.OrdinalIgnoreCase)
            .Select(i => $"  [{CodeText(i.ProblemCode)}] {i.DeviceId}"));
        return lines;
    }
}
