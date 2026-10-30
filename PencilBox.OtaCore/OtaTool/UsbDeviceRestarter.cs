// <copyright file="UsbDeviceRestarter.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Diagnostics;

namespace PencilBox.Core.OtaTool;

/// <summary>
/// pnputil /restart-device 重启 USB 设备（=软件拔插）——异常台自动重置的执行端。
/// 仅 Win10 2004+（19041，部署地板即此）；**需管理员权限**，非提权运行时 pnputil 拒绝访问，
/// 调用方按失败输出给指引（管理员运行或手动拔插）。实例 ID 含 &amp;，必须整体加引号。
/// </summary>
internal static class UsbDeviceRestarter
{
    /// <summary>重启单个设备实例。不抛异常：失败以 (false, 输出) 返回，绝不阻塞其余台。</summary>
    public static async Task<(bool Ok, string Output)> RestartAsync(string instanceId)
    {
        try
        {
            var psi = new ProcessStartInfo("pnputil", $"/restart-device \"{instanceId}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null)
                return (false, "pnputil 启动失败");
            var output = await p.StandardOutput.ReadToEndAsync() + await p.StandardError.ReadToEndAsync();
            await Task.Run(() => p.WaitForExit(15000));
            return (p.ExitCode == 0, output.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
