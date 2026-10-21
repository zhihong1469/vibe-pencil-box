// <copyright file="DeviceCommands.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using Microsoft.Extensions.Logging;
using PencilBox.Cli.Output;
using PencilBox.Core.Ble;
using PencilBox.Core.Communication;
using PencilBox.Core.Protocol;
using PencilBox.Core.Services;

namespace PencilBox.Cli.Commands;

public class DeviceCommands
{
    private readonly IDeviceService _deviceService;
    private readonly IHidTransport _hidTransport;
    private readonly IBleScanner _bleScanner;
    private readonly ITargetBindSender _targetBindSender;
    private readonly IPenBindingService _penBindingService;

    public DeviceCommands(IDeviceService deviceService, IHidTransport hidTransport, IBleScanner bleScanner,
        ITargetBindSender targetBindSender, IPenBindingService penBindingService)
    {
        _deviceService = deviceService;
        _hidTransport = hidTransport;
        _bleScanner = bleScanner;
        _targetBindSender = targetBindSender;
        _penBindingService = penBindingService;
    }

    public async Task<int> ScanAsync(ushort? vid, ushort? pid)
    {
        var devices = await _hidTransport.EnumerateDevicesAsync();
        
        var filtered = devices.AsEnumerable();
        if (vid.HasValue)
            filtered = filtered.Where(d => d.VendorId == vid.Value);
        if (pid.HasValue)
            filtered = filtered.Where(d => d.ProductId == pid.Value);

        var list = filtered.ToList();
        ConsoleFormatter.Info($"Found {list.Count} HID device(s)");

        ConsoleFormatter.PrintTable(list,
            ("O", d => d.IsOurDevice ? "本" : ""),
            ("T", d => d.IsCommandChannel ? "★" : ""),
            ("VID", d => $"0x{d.VendorId:X4}"),
            ("PID", d => $"0x{d.ProductId:X4}"),
            ("序列号", d => string.IsNullOrEmpty(d.SerialNumber) ? "-" : d.SerialNumber),
            ("接口", d => d.InterfaceLabel),
            ("UsagePg", d => d.IsCommandChannel ? $"0x{d.UsagePage:X4}" : "-"),
            ("IN/OUT", d => $"{d.InputReportLength}/{d.OutputReportLength}"),
            ("Path", d => TruncatePath(d.DevicePath)));

        var ourCmd = list.Count(d => d.IsOurDevice && d.IsCommandChannel);
        var ourDev = list.Count(d => d.IsOurDevice);
        if (ourCmd > 0)
        {
            // 多实例差异化：命令通道按序列号逐台列出（同插多支时个体可辨，02 号）
            var serials = list.Where(d => d.IsOurDevice && d.IsCommandChannel)
                .Select((d, i) => $"[{i + 1}]{(string.IsNullOrEmpty(d.SerialNumber) ? "(无序列号)" : d.SerialNumber)}");
            ConsoleFormatter.Success($"本产品 EP4 命令通道(0xFF02)接口: {ourCmd} 台：{string.Join("、", serials)}；本产品设备接口共 {ourDev} 个");
            if (ourCmd > 1)
                ConsoleFormatter.Warn("多台同插：company 域请用 --serial <SN> 选台（缺省会拒绝执行避免刷错设备）");
        }
        else
            ConsoleFormatter.Info("本产品设备无 EP4 命令通道(0xFF02)接口");

        return 0;
    }

    public async Task<int> ConnectAsync(string? devicePath, ushort? vid, ushort? pid)
    {
        if (string.IsNullOrEmpty(devicePath) && (vid.HasValue || pid.HasValue))
        {
            var devices = await _hidTransport.EnumerateDevicesAsync();
            var match = devices.FirstOrDefault(d => 
                (!vid.HasValue || d.VendorId == vid.Value) &&
                (!pid.HasValue || d.ProductId == pid.Value));
            if (match == null)
            {
                ConsoleFormatter.Error("No device matching VID/PID found");
                return 1;
            }
            devicePath = match.DevicePath;
        }

        if (string.IsNullOrEmpty(devicePath))
        {
            ConsoleFormatter.Error("Device path or VID/PID required");
            return 1;
        }

        var connected = await _hidTransport.ConnectAsync(devicePath);
        if (connected)
        {
            ConsoleFormatter.Success($"Connected: {_hidTransport.CurrentDevice?.ProductName}");
            await Task.Delay(200);
            await _deviceService.ConnectAsync(devicePath);
        }
        else
        {
            ConsoleFormatter.Error("Failed to connect");
            return 1;
        }
        return 0;
    }

    public async Task<int> InfoAsync()
    {
        if (!_deviceService.IsConnected)
        {
            ConsoleFormatter.Error("Not connected. Use 'pencilbox device connect' first.");
            return 1;
        }

        var info = await _deviceService.GetDeviceInfoAsync();
        if (info == null)
        {
            ConsoleFormatter.Info("No device info available (timeout or no response)");
            return 0;
        }

        ConsoleFormatter.PrintObject(info);
        return 0;
    }

    public async Task<int> DisconnectAsync()
    {
        await _deviceService.DisconnectAsync();
        ConsoleFormatter.Success("Disconnected");
        return 0;
    }

    public async Task<int> MonitorAsync(byte? reportId, int duration)
    {
        if (!_hidTransport.IsConnected)
        {
            ConsoleFormatter.Error("Not connected. Use 'pencilbox device connect' first.");
            return 1;
        }

        ConsoleFormatter.Info($"Monitoring HID reports for {duration}s... (Ctrl+C to stop)");

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(duration));
        _hidTransport.ReportReceived += (sender, e) =>
        {
            if (reportId.HasValue && e.ReportId != reportId.Value)
                return;

            var hexStr = string.Join(" ", e.Data.Take(16).Select(b => $"{b:X2}"));
            var decoded = HidReportDecoder.DecodeAuto(e.ReportId, e.Data);
            Console.WriteLine($"[{e.Timestamp:HH:mm:ss.fff}] RID=0x{e.ReportId:X2} [{e.Data.Length,3}B] {hexStr}{(e.Data.Length > 16 ? "..." : "")}");
            if (!string.IsNullOrEmpty(decoded))
                Console.WriteLine($"  => {decoded}");
        };

        try { await Task.Delay(Timeout.Infinite, cts.Token); }
        catch (OperationCanceledException) { }

        ConsoleFormatter.Info("Monitor stopped");
        return 0;
    }

    public async Task<int> SendRawAsync(byte reportId, string hexData, bool legacy = false, int waitMs = 600)
    {
        if (!_hidTransport.IsConnected)
        {
            var rc = await ConnectTargetAsync(legacy: legacy);
            if (rc != 0)
                return rc;
        }

        var data = ParseHex(hexData);

        // 收集回包（带 RID 设备：上行带报告 ID）
        var received = new List<(byte Rid, byte[] Data)>();
        void Handler(object? s, HidReportReceivedEventArgs e) => received.Add((e.ReportId, e.Data));
        _hidTransport.ReportReceived += Handler;
        try
        {
            await _hidTransport.SendReportAsync(reportId, data);
            ConsoleFormatter.Success($"Sent {data.Length} bytes on RID 0x{reportId:X2}");
            await Task.Delay(waitMs);
        }
        finally
        {
            _hidTransport.ReportReceived -= Handler;
        }

        if (received.Count == 0)
        {
            ConsoleFormatter.Info($"No response within {waitMs}ms");
            return 0;
        }

        foreach (var (rid, rdata) in received)
        {
            var hex = string.Join(" ", rdata.Select(b => $"{b:X2}"));
            ConsoleFormatter.Info($"Rsp RID=0x{rid:X2} [{rdata.Length}B]: {hex}");
        }
        return 0;
    }

    /// <summary>
    /// 自动连接：优先按 UsagePage/VID/PID 匹配目标 Dongle 命令接口。
    /// legacy=true 时连旧 0xA0/0x05 接口（65/65 非 0xFF02 的厂商接口，带 RID）。
    /// </summary>
    public async Task<int> ConnectTargetAsync(ushort? vid = null, ushort? pid = null, bool legacy = false)
    {
        var devices = await _hidTransport.EnumerateDevicesAsync();
        vid ??= ProtocolConstants.TargetVendorId;
        pid ??= ProtocolConstants.TargetProductId;

        var list = devices.Where(d => d.VendorId == vid && d.ProductId == pid).ToList();
        HidDeviceInfo? match;
        if (legacy)
        {
            // 旧 0xA0/0x05 通道：非 0xFF02 的 65/65 厂商接口（带 RID 承载）
            match = list.FirstOrDefault(d => d.UsagePage != ProtocolConstants.UsagePageCustomHid
                                             && d.InputReportLength >= 64);
        }
        else
        {
            match = list.FirstOrDefault(d => d.UsagePage == ProtocolConstants.UsagePageCustomHid)
                    ?? list.FirstOrDefault();
        }

        if (match == null)
        {
            ConsoleFormatter.Error("No target dongle interface found");
            return 1;
        }

        if (!await _hidTransport.ConnectAsync(match.DevicePath))
        {
            ConsoleFormatter.Error("Failed to connect");
            return 1;
        }
        ConsoleFormatter.Info($"Connected iface: {match.InterfaceLabel}");
        return 0;
    }

    public async Task<int> SendFeatureAsync(byte reportId, string hexData, bool legacy = false, int waitMs = 900)
    {
        if (!_hidTransport.IsConnected)
        {
            var rc = await ConnectTargetAsync(legacy: legacy);
            if (rc != 0)
                return rc;
        }

        var data = ParseHex(hexData);

        var received = new List<(byte Rid, byte[] Data)>();
        void Handler(object? s, HidReportReceivedEventArgs e) => received.Add((e.ReportId, e.Data));
        _hidTransport.ReportReceived += Handler;
        try
        {
            await _hidTransport.SendFeatureReportAsync(reportId, data);
            ConsoleFormatter.Success($"Sent Feature Report {data.Length} bytes on RID 0x{reportId:X2}");
            await Task.Delay(waitMs);
        }
        finally
        {
            _hidTransport.ReportReceived -= Handler;
        }

        if (received.Count == 0)
        {
            ConsoleFormatter.Info($"No response within {waitMs}ms");
            return 0;
        }

        foreach (var (rid, rdata) in received)
        {
            var hex = string.Join(" ", rdata.Select(b => $"{b:X2}"));
            ConsoleFormatter.Info($"Rsp RID=0x{rid:X2} [{rdata.Length}B]: {hex}");
        }
        return 0;
    }

    // ==================== EP4（custom_hid 无 RID 命令通道）测试 ====================

    public async Task<int> Ep4ConnectAsync(ushort? vid, ushort? pid)
    {
        if (!_hidTransport.IsConnected)
        {
            var devices = await _hidTransport.EnumerateDevicesAsync();
            // 目标 = 0xFF02 主特征；VID/PID 仅弱过滤（产品 0x2F70/0xEA11 + 过渡期旧 0x5553/0x4155）
            var match = devices.FirstOrDefault(d =>
                    d.UsagePage == ProtocolConstants.UsagePageCustomHid &&
                    ProtocolConstants.IsTargetVidPid(d.VendorId, d.ProductId))
                ?? devices.FirstOrDefault(d =>
                    d.IsTargetDongle &&
                    ProtocolConstants.IsTargetVidPid(d.VendorId, d.ProductId));

            if (match == null)
            {
                ConsoleFormatter.Error("No EP4 (0xFF02) command-channel device found");
                return 1;
            }

            if (!await _hidTransport.ConnectAsync(match.DevicePath))
            {
                ConsoleFormatter.Error("Failed to connect to EP4 device");
                return 1;
            }
        }

        var dev = _hidTransport.CurrentDevice;
        var bearer = _hidTransport.IsNoReportIdDevice ? "no-RID" : "RID";
        ConsoleFormatter.Success($"EP4 connected: {dev?.ProductName} " +
            $"({bearer}), UsagePage=0x{dev?.UsagePage:X4}");
        return 0;
    }

    public async Task<int> Ep4SendAsync(string? cmdHex, string? macHex, string? rawHex, bool useBle, int waitMs)
    {
        if (!_hidTransport.IsConnected)
        {
            var rc = await Ep4ConnectAsync(ProtocolConstants.TargetVendorId, ProtocolConstants.TargetProductId);
            if (rc != 0)
                return rc;
        }

        byte[] frame;
        if (!string.IsNullOrEmpty(rawHex))
        {
            frame = ParseHex(rawHex);
        }
        else if (!string.IsNullOrEmpty(cmdHex))
        {
            var cmdBytes = ParseHex(cmdHex);
            var cmd = cmdBytes.Length > 0 ? cmdBytes[0] : (byte)0;

            // --ble + A4(PAIR_SET_TARGET)/A2(PAIR_NAME)：走 USB/协议方接口（与 UI 单一事实源）
            if (useBle)
            {
                if (cmd == (byte)CommandId.PairSetTarget)
                {
                    var best = await _bleScanner.RecommendAsync(5);
                    if (best == null)
                    {
                        ConsoleFormatter.Info("No SunVote pen broadcast detected for A4 binding");
                        return 1;
                    }
                    ConsoleFormatter.Info($"BLE 推荐目标: {best.MacDisplayText} \"{best.LocalName}\"（显示序直传，PC 零转换）");
                    var res = await _targetBindSender.SendBindTargetAsync(
                        best.MacDisplayOrder, PencilBox.Core.Ble.BleNaming.BuildBtName(best.MacDisplayOrder));
                    ConsoleFormatter.Info(res.Message);
                    return res.Success ? 0 : 1;
                }

                if (cmd == (byte)CommandId.PairName)
                {
                    var res = await _targetBindSender.StartNamePairingAsync();
                    ConsoleFormatter.Info(res.Message);
                    return res.Success ? 0 : 1;
                }
            }

            // 通用帧路径（A1/A3/raw/其它）：仍由本应用层直接组帧发送
            byte[] data;
            if (useBle)
            {
                var best = await _bleScanner.RecommendAsync(5);
                if (best == null)
                {
                    ConsoleFormatter.Info("No SunVote pen broadcast detected for injection");
                    return 1;
                }
                data = best.MacLinkOrder;
                ConsoleFormatter.Info($"BLE 推荐目标: {best.MacDisplayText} \"{best.LocalName}\" (链路序 {best.MacLinkText})");
            }
            else
            {
                data = ParseHex(macHex ?? string.Empty);
            }

            frame = CommandFrame.Build(HidChannel.PcToDongle, (CommandId)cmd, data);
        }
        else
        {
            ConsoleFormatter.Error("Need --cmd (A1/A2/A3/A4) or --raw hex frame");
            return 1;
        }

        ConsoleFormatter.Info($"Write EP4 [{frame.Length}B]: {ToHex(frame)}");

        var received = new List<byte[]>();
        void Handler(object? s, HidReportReceivedEventArgs e) => received.Add(e.Data);
        _hidTransport.ReportReceived += Handler;
        try
        {
            await _hidTransport.SendReportAsync(0, frame);
            await Task.Delay(waitMs);
        }
        finally
        {
            _hidTransport.ReportReceived -= Handler;
        }

        if (received.Count == 0)
        {
            ConsoleFormatter.Info("No EP4 response received (within wait window)");
            return 0;
        }

        foreach (var r in received)
        {
            ConsoleFormatter.Info($"Rsp EP4 [{r.Length}B]: {ToHex(r)}");
            if (CommandFrame.TryParse(r, out var ch, out var cmdId, out var payload))
            {
                PrintEp4Response(cmdId, payload);
            }
            else
            {
                ConsoleFormatter.Info("  => not a valid 4A4C frame (raw only)");
            }
        }
        return 0;
    }

    /// <summary>
    /// 绑定目标笔端到 Dongle —— 与 UI "绑定"按钮同一方法栈（IPenBindingService.BindPenByMacAsync）：
    /// 目录 KnownPen（显示序 MAC）→ 链路序（大小端自动）→ ITargetBindSender → DongleCommandClient(A4, 等 status)。
    /// </summary>
    public async Task<int> Ep4BindAsync(string? macDisplay)
    {
        if (string.IsNullOrWhiteSpace(macDisplay))
        {
            ConsoleFormatter.Error("Need --mac <AA:BB:CC:DD:EE:FF 或 AABBCCDDEEFF>（显示序）");
            return 1;
        }

        if (!_hidTransport.IsConnected)
        {
            var rc = await Ep4ConnectAsync(ProtocolConstants.TargetVendorId, ProtocolConstants.TargetProductId);
            if (rc != 0)
                return rc;
        }

        byte[] display;
        try { display = PencilBox.Core.Ble.MacAddress.ParseDisplay(macDisplay); }
        catch (Exception ex)
        {
            ConsoleFormatter.Error($"MAC 解析失败：{ex.Message}");
            return 1;
        }

        var pen = new KnownPen
        {
            MacDisplayHex = PencilBox.Core.Ble.MacAddress.FormatDisplay(display).Replace(":", ""),
            LocalName = null,
            IsPresent = false
        };
        ConsoleFormatter.Info($"绑定目标（显示序直传，PC 零转换）：{pen.MacDisplayText}  NAME=\"{pen.EffectiveBtName}\"");

        var res = await _penBindingService.BindPenByMacAsync(pen);
        ConsoleFormatter.Info(res.Message);
        return res.Success ? 0 : 1;
    }

    public async Task<int> Ep4MonitorAsync(int duration)
    {
        if (!_hidTransport.IsConnected)
        {
            var rc = await Ep4ConnectAsync(ProtocolConstants.TargetVendorId, ProtocolConstants.TargetProductId);
            if (rc != 0)
                return rc;
        }

        ConsoleFormatter.Info($"Monitoring EP4 raw frames for {duration}s... (Ctrl+C to stop)");
        int count = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(duration));
        _hidTransport.ReportReceived += (sender, e) =>
        {
            count++;
            Console.WriteLine($"[{e.Timestamp:HH:mm:ss.fff}] EP4 [{e.Data.Length}B]: {ToHex(e.Data)}");
        };
        try { await Task.Delay(Timeout.Infinite, cts.Token); }
        catch (OperationCanceledException) { }
        ConsoleFormatter.Info($"Monitor stopped, {count} frame(s)");
        return 0;
    }

    private static void PrintEp4Response(CommandId cmdId, byte[] payload)
    {
        if (payload.Length < 1)
            return;
        byte status = payload[0];
        ConsoleFormatter.Info($"  => CMD=0x{(byte)cmdId:X2}, status=0x{status:X2}" +
            $" ({StatusText(status)})");

        // PAIR_GET_STATE(0xA1)：payload = [status][paired][connected][mode][MAC×6][name_len][name…]
        if (cmdId == CommandId.PairGetState && payload.Length >= 2)
        {
            var paired = payload.Length > 1 ? payload[1] : (byte)0;
            var connected = payload.Length > 2 ? payload[2] : (byte)0;
            var mode = payload.Length > 3 ? payload[3] : (byte)0;
            var macOff = 4;
            bool hasMac = payload.Length >= macOff + 6;
            string mac = hasMac ? ToHex(payload.AsSpan(macOff, 6).ToArray()) : "-";
            var nameLen = hasMac && payload.Length > macOff + 6 ? payload[macOff + 6] : (byte)0;
            var name = nameLen > 0 && payload.Length > macOff + 7
                ? System.Text.Encoding.ASCII.GetString(payload, macOff + 7, nameLen)
                : string.Empty;
            ConsoleFormatter.Info($"    paired={paired} connected={connected} mode=0x{mode:X2}");
            ConsoleFormatter.Info($"    mac={mac} name_len={nameLen} name=\"{name}\"");
        }
    }

    private static string StatusText(byte status) => status switch
    {
        0x00 => "OK",
        0x01 => "PARAM(参数无效)",
        0x02 => "NOT_FOUND/表满",
        0x03 => "FLASH(写失败)",
        0x04 => "PEN_NOT_CONNECTED(笔未连接)",
        0x05 => "UNSUPPORT",
        0x06 => "TIMEOUT",
        0x7F => "UNKNOWN",
        _ => $"0x{status:X2}"
    };

    private static string ToHex(byte[] bytes) =>
        string.Join(" ", bytes.Select(b => $"{b:X2}"));

    private static byte[] ParseHex(string hex)
    {
        var cleaned = hex.Replace(" ", "").Replace("\t", "");
        var bytes = new byte[cleaned.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(cleaned.Substring(i * 2, 2), 16);
        return bytes;
    }

    private static string TruncatePath(string path)
    {
        return path.Length > 50 ? "..." + path[^47..] : path;
    }
}
