// <copyright file="UsbProblemProbe.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Management;
using PencilBox.Core.Communication;
using PencilBox.Core.Protocol;

namespace PencilBox.Core.OtaTool;

/// <summary>
/// WMI 盘点 USB 设备层本产品实例（含配置失败台）——6 插 4 识别问题的取数端：
/// HID 枚举只看得见起得来的，"插着却不识别"的台（Code=10 无法启动等）只能在这层看到。
/// Win32_PnPEntity 实例 ID 尾段=USB 口路径（如 7&amp;1F9D7D9F&amp;0&amp;1），同口必同段，可作异常台定位标识。
/// 取数可能数百毫秒（WMI 服务冷启动），调用方须放后台线程。
/// </summary>
internal static class UsbProblemProbe
{
    /// <summary>列本产品 VID（含旧固件过渡 VID）的在位 USB 实例（复合根+MI 子接口全量，分类在 UsbProblemReport）；
    /// 不在位的幽灵残留（Present=false）不盘。WQL LIKE 中反斜杠是转义符，须写 \\ 才匹配字面 \（真机 10-30 验证）。</summary>
    public static List<UsbPnpInstance> EnumerateOurs()
    {
        var result = new List<UsbPnpInstance>();
        var vids = new[] { ProtocolConstants.TargetVendorId, ProtocolConstants.LegacyVendorId }
            .Distinct()
            .Select(v => $@"USB\\VID_{v:X4}%");
        var where = string.Join(" OR ", vids.Select(v => $"DeviceID LIKE '{v}'"));
        using var searcher = new ManagementObjectSearcher(
            $"SELECT DeviceID, ConfigManagerErrorCode, Present FROM Win32_PnPEntity WHERE {where}");
        foreach (var o in searcher.Get())
        {
            var present = o["Present"];
            if (present != null && !Convert.ToBoolean(present))
                continue;
            result.Add(new UsbPnpInstance(
                o["DeviceID"] as string ?? "",
                Convert.ToInt32(o["ConfigManagerErrorCode"] ?? 0)));
        }
        return result;
    }
}
