// <copyright file="BleScanner.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using Microsoft.Extensions.Logging;
using PencilBox.Core.Ble;
using PencilBox.Core.Communication;
using Windows.Devices.Bluetooth.Advertisement;

namespace PencilBox.Infrastructure.Ble;

/// <summary>
/// 基于 Windows WinRT BLE 广播的扫描实现（无第三方依赖，官方 API）。
/// 负责枚举去重的广播事件，识别 SunVote 笔端并填好显示序/链路序 MAC。
/// 仅平台相关逻辑；识别/推荐算法在 Core（<see cref="BleNaming"/>），便于单测。
/// </summary>
public class BleScanner : IBleScanner
{
    private readonly ILogger<BleScanner> _logger;

    public BleScanner(ILogger<BleScanner> logger)
    {
        _logger = logger;
    }

    public Task<IReadOnlyList<BleDeviceInfo>> ScanAsync(int durationSec = 5,
        CancellationToken cancellationToken = default)
        => RunScanAsync(durationSec, cancellationToken);

    public async Task<BleDeviceInfo?> RecommendAsync(int durationSec = 5,
        CancellationToken cancellationToken = default)
    {
        var all = await ScanAsync(durationSec, cancellationToken);
        return BleNaming.Recommend(all);
    }

    private async Task<IReadOnlyList<BleDeviceInfo>> RunScanAsync(int durationSec,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<ulong, BleDeviceInfo>();
        var tcs = new TaskCompletionSource<IReadOnlyList<BleDeviceInfo>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        if (durationSec <= 0)
            durationSec = 5;

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        void OnReceived(BluetoothLEAdvertisementWatcher w, BluetoothLEAdvertisementReceivedEventArgs e)
        {
            try
            {
                if (e.BluetoothAddress == 0)
                    return;

                // 广播地址：Windows 给出的是"地址数值"（低位为广播地址最低字节）。
                // 这里统一解析为显示序字节（最高字节在前），并在 BleDeviceInfo 内转链路序。
                var display = new byte[6];
                ulong addr = e.BluetoothAddress;
                for (int i = 0; i < 6; i++)
                    display[i] = (byte)((addr >> (8 * (5 - i))) & 0xFF);   // display[0]=最高字节

                string? name = e.Advertisement.LocalName;
                if (string.IsNullOrEmpty(name))
                    name = ReadNameFromScanData(e.Advertisement);

                var isPen = BleNaming.IsSunVotePen(name);
                var pairing = BleNaming.IsPairingMode(name);
                var macSuffix = BleNaming.TryGetMacSuffix(name);

                var info = new BleDeviceInfo
                {
                    MacDisplayOrder = display,
                    MacLinkOrder = MacAddress.ToLinkOrder(display),
                    LocalName = name,
                    Rssi = e.RawSignalStrengthInDBm,
                    IsSunVotePen = isPen,
                    IsPairingMode = pairing,
                    MacSuffix = macSuffix,
                    AddressType = MapAddressType(e.BluetoothAddressType)   // 建连必带（Random/Public 试探可省一跳）
                };

                // 同一地址去重：保留 RSSI 更强 / 更多信息的条目
                if (result.TryGetValue(e.BluetoothAddress, out var existing))
                {
                    if (e.RawSignalStrengthInDBm > existing.Rssi)
                        result[e.BluetoothAddress] = info;
                }
                else
                {
                    result[e.BluetoothAddress] = info;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process BLE advertisement");
            }
        }

        watcher.Received += OnReceived;
        watcher.Stopped += (s, a) =>
        {
            if (!tcs.Task.IsCompleted && !cancellationToken.IsCancellationRequested)
                tcs.TrySetResult(result.Values.ToList());
        };

        try
        {
            try
            {
                watcher.Start();
                _logger.LogDebug("BLE scan started for {Sec}s", durationSec);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start BLE advertisement watcher (no adapter?)");
                return Array.Empty<BleDeviceInfo>();
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(durationSec));

            try
            {
                return await tcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                return result.Values.ToList();
            }
        }
        finally
        {
            watcher.Received -= OnReceived;
            try { watcher.Stop(); } catch { /* ignore */ }
            _logger.LogDebug("BLE scan stopped, {Count} device(s)", result.Count);
        }
    }

    /// <summary>WinRT 地址类型 → Core 枚举（Public/Random 采集给建连用，Unspecified 归 Unknown）。</summary>
    private static Core.Communication.BleAddressType MapAddressType(Windows.Devices.Bluetooth.BluetoothAddressType t) => t switch
    {
        Windows.Devices.Bluetooth.BluetoothAddressType.Public => Core.Communication.BleAddressType.Public,
        Windows.Devices.Bluetooth.BluetoothAddressType.Random => Core.Communication.BleAddressType.Random,
        _ => Core.Communication.BleAddressType.Unknown
    };

    /// <summary>
    /// LocalName 往往在广播（Advertising）而非扫描响应里；作为兜底，从 ScanData 里解析完整本地名。
    /// </summary>
    private static string? ReadNameFromScanData(BluetoothLEAdvertisement advertisement)
    {
        foreach (var data in advertisement.DataSections)
        {
            if (data.DataType == BluetoothLEAdvertisementDataTypes.CompleteLocalName
                || data.DataType == BluetoothLEAdvertisementDataTypes.ShortenedLocalName)
            {
                // DataSection.Data 不含类型/长度前缀，本身即 UTF-8 名字（IBuffer → byte[]）
                 var buf = (Windows.Storage.Streams.IBuffer?)data.Data;
                 if (buf != null)
                 {
                     var bytes = new byte[buf.Length];
                     using var reader = Windows.Storage.Streams.DataReader.FromBuffer(buf);
                     reader.ReadBytes(bytes);
                     return System.Text.Encoding.UTF8.GetString(bytes);
                 }
            }
        }
        return null;
    }
}
