using NAudio.CoreAudioApi;
using System.Management;
using PencilBox.Core.Models;

namespace PencilBox.Infrastructure.Audio;

/// <summary>
/// 枚举音频输入设备并识别 USB Audio 1.0 设备
/// </summary>
public static class AudioDeviceEnumerator
{
    /// <summary>
    /// 获取所有可用录音设备，并标注 USB 信息
    /// </summary>
    public static IReadOnlyList<AudioDeviceInfo> GetCaptureDevices()
        => EnumerateDevices(DataFlow.Capture);

    /// <summary>
    /// 获取所有可用播放设备，并标注 USB 信息（扩音渲染端选择用）
    /// </summary>
    public static IReadOnlyList<AudioDeviceInfo> GetRenderDevices()
        => EnumerateDevices(DataFlow.Render);

    /// <summary>
    /// 按数据流方向枚举活动端点，并经 WMI 标注 USB 信息
    /// </summary>
    private static IReadOnlyList<AudioDeviceInfo> EnumerateDevices(DataFlow flow)
    {
        // 先通过 WMI 获取 USB 音频设备的 VID/PID 与设备 ID 映射
        var usbAudioDevices = GetUsbAudioDeviceMappings();

        var result = new List<AudioDeviceInfo>();

        // 每次重新创建 enumerator，确保设备插拔后能看到最新列表
        using var enumerator = new MMDeviceEnumerator();

        // NAudio 的端点列表
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            var info = new AudioDeviceInfo
            {
                DeviceId = device.ID,
                FriendlyName = device.FriendlyName,
                IsUsbDevice = false
            };

            // 尝试匹配 WMI 中的 USB 信息
            var usbInfo = usbAudioDevices.FirstOrDefault(u =>
                MatchWmiDeviceIdToMmDevice(u.Key, device.ID));

            if (usbInfo.Value != null)
            {
                info.IsUsbDevice = true;
                info.VendorId = usbInfo.Value.VendorId;
                info.ProductId = usbInfo.Value.ProductId;
                info.UsbDevicePath = usbInfo.Value.DeviceId;
            }
            else if (device.FriendlyName.Contains("USB", StringComparison.OrdinalIgnoreCase))
            {
                // 兜底：友好名称带 USB 字样的也标为 USB
                info.IsUsbDevice = true;
            }

            result.Add(info);
        }

        return result;
    }

    /// <summary>
    /// 通过 WMI 查询 USB Audio 设备信息
    /// </summary>
    private static Dictionary<string, UsbAudioMapping> GetUsbAudioDeviceMappings()
    {
        var result = new Dictionary<string, UsbAudioMapping>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Win32_SoundDevice 包含 Windows 识别的声卡设备
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Name, PNPDeviceID FROM Win32_SoundDevice");

            foreach (ManagementObject obj in searcher.Get())
            {
                string? deviceId = obj["DeviceID"]?.ToString();
                string? name = obj["Name"]?.ToString();
                string? pnpDeviceId = obj["PNPDeviceID"]?.ToString();

                if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(pnpDeviceId))
                    continue;

                // PNPDeviceID 形如：USB\VID_1234&PID_5678&MI_00\6&1A2B3C4D&0&0000
                var ids = ParseVidPid(pnpDeviceId);
                if (ids == null)
                    continue;

                result[deviceId] = new UsbAudioMapping
                {
                    DeviceId = deviceId,
                    Name = name ?? string.Empty,
                    PnpDeviceId = pnpDeviceId,
                    VendorId = ids.Value.Vid,
                    ProductId = ids.Value.Pid
                };
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WMI query failed: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// 从 PNPDeviceID 解析 VID/PID
    /// </summary>
    private static (ushort Vid, ushort Pid)? ParseVidPid(string pnpDeviceId)
    {
        try
        {
            string upper = pnpDeviceId.ToUpperInvariant();
            int vidIndex = upper.IndexOf("VID_", StringComparison.Ordinal);
            int pidIndex = upper.IndexOf("PID_", StringComparison.Ordinal);

            if (vidIndex < 0 || pidIndex < 0)
                return null;

            string vidStr = upper.Substring(vidIndex + 4, 4);
            string pidStr = upper.Substring(pidIndex + 4, 4);

            if (ushort.TryParse(vidStr, System.Globalization.NumberStyles.HexNumber, null, out ushort vid) &&
                ushort.TryParse(pidStr, System.Globalization.NumberStyles.HexNumber, null, out ushort pid))
            {
                return (vid, pid);
            }
        }
        catch
        {
            // ignore parse errors
        }

        return null;
    }

    /// <summary>
    /// 尝试把 WMI 的 DeviceID 与 NAudio 的 MMDevice ID 关联
    /// </summary>
    private static bool MatchWmiDeviceIdToMmDevice(string wmiDeviceId, string mmDeviceId)
    {
        // 两者格式不同，做宽松匹配：MMDevice ID 中包含 WMI DeviceID 中的关键部分
        if (string.IsNullOrWhiteSpace(wmiDeviceId) || string.IsNullOrWhiteSpace(mmDeviceId))
            return false;

        // 取 WMI DeviceID 中反斜杠后的最后一段（实例 ID）
        string wmiKey = wmiDeviceId.Trim().Replace("\\", "/").ToUpperInvariant();
        string mmKey = mmDeviceId.Trim().Replace("\\", "/").ToUpperInvariant();

        // 互相包含或关键部分匹配
        return mmKey.Contains(wmiKey) || wmiKey.Contains(mmKey);
    }

    private class UsbAudioMapping
    {
        public string DeviceId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string PnpDeviceId { get; set; } = string.Empty;
        public ushort VendorId { get; set; }
        public ushort ProductId { get; set; }
    }
}
