// <copyright file="AudioEndpointControl.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using NAudio.CoreAudioApi;

namespace PencilBox.Infrastructure.Audio;

/// <summary>端点音量信息快照</summary>
public sealed record EndpointVolumeInfo(string Role, string Name, int VolumePercent, bool Mute);

/// <summary>
/// 系统端点（扬声器/麦克风）音量与静音控制（Windows AudioEndpointVolume）。
/// 用途：扩音/录音前的自检与排障——"跑了没声"多半是端点静音/音量 0，不是引擎问题。
/// selector 按友好名称子串匹配（如 "Realtek"）；null = 该流向的系统默认设备。
/// 注意：默认采集设备可能是远程控制类软件的虚拟声卡，测真实麦克风必须显式指定。
/// </summary>
public static class AudioEndpointControl
{
    public static (EndpointVolumeInfo Render, EndpointVolumeInfo Capture) Describe(string? outSelector, string? inSelector)
    {
        return (DescribeOne(isRender: true, outSelector), DescribeOne(isRender: false, inSelector));
    }

    public static EndpointVolumeInfo DescribeOne(bool isRender, string? selector)
    {
        using var d = Resolve(isRender, selector);
        return new EndpointVolumeInfo(
            isRender ? "扬声器" : "麦克风",
            d.FriendlyName,
            (int)Math.Round(d.AudioEndpointVolume.MasterVolumeLevelScalar * 100f),
            d.AudioEndpointVolume.Mute);
    }

    /// <summary>设置音量（0~100；负数=不动音量）与静音（null=不改动）</summary>
    public static void SetVolume(bool isRender, string? selector, int percent, bool? mute)
    {
        using var d = Resolve(isRender, selector);
        if (percent >= 0)
            d.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(percent, 0, 100) / 100f;
        if (mute.HasValue) d.AudioEndpointVolume.Mute = mute.Value;
    }

    private static MMDevice Resolve(bool isRender, string? selector)
    {
        var flow = isRender ? DataFlow.Render : DataFlow.Capture;
        using var e = new MMDeviceEnumerator();
        if (string.IsNullOrWhiteSpace(selector))
            return e.GetDefaultAudioEndpoint(flow, Role.Console);
        var hit = e.EnumerateAudioEndPoints(flow, DeviceState.Active)
                   .FirstOrDefault(d => d.FriendlyName.Contains(selector, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException($"未找到匹配的端点: {selector}（flow={flow}）");
        return hit;
    }
}
