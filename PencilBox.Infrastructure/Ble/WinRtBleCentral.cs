// <copyright file="WinRtBleCentral.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using Microsoft.Extensions.Logging;
using PencilBox.Core.Ble;
using PencilBox.Core.Communication;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace PencilBox.Infrastructure.Ble;

/// <summary>
/// BLE 中心设备（Central/GATT Client）的 WinRT 实现——PC 侧"类基站"测试位。
/// 职责：建连（GAP）→ 服务发现（GATT 全表枚举）→ 读/写/订阅 → SMP 配对/退配。
/// 平台细节：
/// - <c>FromBluetoothAddressAsync</c> 的地址类型必须与广播一致，否则返回 null → Unknown 时按 Random→Public 试探；
/// - 枚举/读写一律 <c>BluetoothCacheMode.Uncached</c>，防系统缓存读到旧 GATT 表（笔端改固件后必踩）；
/// - 服务/特征对象须持有强引用（订阅期间防 GC 回收导致通知丢失）。
/// </summary>
public sealed class WinRtBleCentral : IBleCentral
{
    private readonly ILogger<WinRtBleCentral> _logger;
    private BluetoothLEDevice? _device;
    private GattSession? _session;
    private readonly List<object> _keepAlive = new();   // GATT 对象强引用

    public WinRtBleCentral(ILogger<WinRtBleCentral> logger) => _logger = logger;

    public bool IsConnected =>
        _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;

    public string? ConnectedMacDisplay =>
        _device == null ? null : MacAddress.FormatDisplay(ToDisplayBytes(_device.BluetoothAddress));

    public event EventHandler<bool>? ConnectionChanged;

    public async Task<BleConnectResult> ConnectAsync(BleConnectRequest request, CancellationToken ct = default)
    {
        await TeardownAsync(preserveEvent: false);   // 复用实例前先清场

        ulong address = ToAddressValue(MacAddress.ParseDisplay(request.MacDisplay));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        BluetoothLEDevice? device = null;
        foreach (var addrType in ResolveOrder(request.AddressType))
        {
            try
            {
                device = await BluetoothLEDevice
                    .FromBluetoothAddressAsync(address, addrType)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromMilliseconds(request.TimeoutMs), ct);
            }
            catch (TimeoutException)
            {
                return BleConnectResult.Fail($"建连超时（{request.TimeoutMs}ms，地址类型 {addrType}）——设备在广播吗？");
            }
            catch (OperationCanceledException)
            {
                return BleConnectResult.Fail("建连已取消");
            }

            if (device != null)
                break;
            _logger.LogDebug("FromBluetoothAddressAsync null with {Type}, trying next", addrType);
        }

        if (device == null)
        {
            return BleConnectResult.Fail(
                "平台返回 null：地址类型不符或设备不在（确认笔端在广播；Random/Public 已各试一次）");
        }

        sw.Stop();
        _device = device;
        device.ConnectionStatusChanged += OnConnectionStatusChanged;

        // MTU + 连接维持（MaintainConnection=true 时断连由平台自动回连，观测回连行为用）
        int mtu = 0;
        try
        {
            var session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
            if (session != null)
            {
                _session = session;
                mtu = session.MaxPduSize;
                session.MaintainConnection = request.MaintainConnection;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GattSession unavailable (MTU/maintain skipped)");
        }

        // 触发真实 ACL 连接：拉一次状态（平台惰性建连，直到访问 GATT 才真正连）
        if (device.ConnectionStatus != BluetoothConnectionStatus.Connected)
        {
            try
            {
                await device.GetGattServicesAsync(BluetoothCacheMode.Uncached)
                    .AsTask().WaitAsync(TimeSpan.FromMilliseconds(Math.Max(3_000, request.TimeoutMs)), ct);
            }
            catch (Exception ex)
            {
                await TeardownAsync(preserveEvent: false);
                return BleConnectResult.Fail($"GATT 通道未就绪（设备离线/不在范围）：{ex.Message}");
            }
        }

        if (device.ConnectionStatus != BluetoothConnectionStatus.Connected)
        {
            await TeardownAsync(preserveEvent: false);
            return BleConnectResult.Fail("建连后状态仍为 Disconnected（对端拒绝或已断开）");
        }

        return new BleConnectResult
        {
            Ok = true,
            MacDisplay = request.MacDisplay,
            Name = device.Name,
            Mtu = mtu,
            ConnectMs = (int)sw.ElapsedMilliseconds
        };
    }

    public async Task DisconnectAsync() => await TeardownAsync(preserveEvent: true);

    public async Task<BleDiscoveryResult> DiscoverAsync(bool uncached = true, CancellationToken ct = default)
    {
        var device = RequireDevice();
        if (device == null)
            return BleDiscoveryResult.Fail("未连接（先 ConnectAsync）");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var services = new List<BleGattServiceInfo>();
        var mode = uncached ? BluetoothCacheMode.Uncached : BluetoothCacheMode.Cached;

        try
        {
            var svcResult = await device.GetGattServicesAsync(mode)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(15), ct);
            if (svcResult.Status != GattCommunicationStatus.Success)
                return BleDiscoveryResult.Fail($"服务枚举失败：{svcResult.Status}（Uncached 需设备在线）");

            foreach (var svc in svcResult.Services)
            {
                _keepAlive.Add(svc);
                var chars = new List<BleGattCharacteristicInfo>();
                var chrResult = await svc.GetCharacteristicsAsync(mode)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct);
                if (chrResult.Status == GattCommunicationStatus.Success)
                {
                    foreach (var chr in chrResult.Characteristics)
                    {
                        _keepAlive.Add(chr);
                        chars.Add(new BleGattCharacteristicInfo
                        {
                            ServiceUuid = svc.Uuid.ToString().ToUpperInvariant(),
                            ServiceHandle = (ushort)svc.AttributeHandle,
                            Uuid = chr.Uuid.ToString().ToUpperInvariant(),
                            Handle = (ushort)chr.AttributeHandle,
                            Properties = (int)chr.CharacteristicProperties,
                            PropertiesText = PropertiesText(chr.CharacteristicProperties)
                        });
                    }
                }
                else
                {
                    _logger.LogWarning("特征枚举失败 svc={Svc}: {Status}", svc.Uuid, chrResult.Status);
                }

                services.Add(new BleGattServiceInfo
                {
                    Uuid = svc.Uuid.ToString().ToUpperInvariant(),
                    Handle = (ushort)svc.AttributeHandle,
                    Characteristics = chars
                });
            }
        }
        catch (Exception ex)
        {
            return BleDiscoveryResult.Fail($"枚举异常：{ex.Message}");
        }

        sw.Stop();
        return new BleDiscoveryResult { Ok = true, Services = services, ElapsedMs = (int)sw.ElapsedMilliseconds };
    }

    public async Task<BleValueResult> ReadAsync(string uuid, CancellationToken ct = default)
    {
        var chr = await FindCharacteristicAsync(uuid, ct);
        if (chr == null)
            return BleValueResult.Fail($"未找到特征 {uuid}（先 discover，或确认 UUID）");

        var read = await chr.ReadValueAsync(BluetoothCacheMode.Uncached)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct);
        if (read.Status != GattCommunicationStatus.Success)
            return BleValueResult.Fail($"读失败：{read.Status}");

        return new BleValueResult { Ok = true, Data = await ToBytesAsync(read.Value) };
    }

    public async Task<BleOpResult> WriteAsync(string uuid, byte[] data, bool withoutResponse = false,
        CancellationToken ct = default)
    {
        var chr = await FindCharacteristicAsync(uuid, ct);
        if (chr == null)
            return BleOpResult.Fail($"未找到特征 {uuid}（先 discover，或确认 UUID）");

        var option = withoutResponse ? GattWriteOption.WriteWithoutResponse : GattWriteOption.WriteWithResponse;
        if (!withoutResponse && (chr.CharacteristicProperties & GattCharacteristicProperties.Write) == 0)
            return BleOpResult.Fail("该特征不支持 Write With Response（properties 无 WRITE 位；试 --no-response）");
        if (withoutResponse && (chr.CharacteristicProperties & GattCharacteristicProperties.WriteWithoutResponse) == 0)
            return BleOpResult.Fail("该特征不支持 Write Without Response（properties 无 WRITE_NRSP 位）");

        var write = await chr.WriteValueAsync(CryptographicBuffer.CreateFromByteArray(data), option)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct);
        return write == GattCommunicationStatus.Success
            ? new BleOpResult { Ok = true }
            : BleOpResult.Fail($"写失败：{write}");
    }

    public async Task<BleOpResult> SubscribeAsync(string uuid, TimeSpan duration, Action<string>? onHex,
        CancellationToken ct = default)
    {
        var chr = await FindCharacteristicAsync(uuid, ct);
        if (chr == null)
            return BleOpResult.Fail($"未找到特征 {uuid}（先 discover，或确认 UUID）");

        bool indicate = (chr.CharacteristicProperties & GattCharacteristicProperties.Indicate) != 0;
        bool notify = (chr.CharacteristicProperties & GattCharacteristicProperties.Notify) != 0;
        if (!notify && !indicate)
            return BleOpResult.Fail("该特征无 NOTIFY/INDICATE 属性，不能订阅");

        int count = 0;
        void OnChanged(GattCharacteristic c, GattValueChangedEventArgs e)
        {
            Interlocked.Increment(ref count);
            onHex?.Invoke(Convert.ToHexString(ToBytes(e.CharacteristicValue)));
        }

        chr.ValueChanged += OnChanged;
        try
        {
            var cccd = await chr.WriteClientCharacteristicConfigurationDescriptorAsync(
                    notify ? GattClientCharacteristicConfigurationDescriptorValue.Notify
                           : GattClientCharacteristicConfigurationDescriptorValue.Indicate)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct);
            if (cccd != GattCommunicationStatus.Success)
                return BleOpResult.Fail($"写 CCCD 失败：{cccd}（特征是否要求加密/绑定？）");

            try { await Task.Delay(duration, ct); }
            catch (OperationCanceledException) { /* 到点/Ctrl+C：正常收尾 */ }
        }
        finally
        {
            chr.ValueChanged -= OnChanged;
            try
            {
                await chr.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "退订 CCCD 失败（连接可能已断）");
            }
        }

        return new BleOpResult { Ok = true, Notifications = count };
    }

    public async Task<BlePairResult> PairAsync(string protection = "encryption", CancellationToken ct = default)
    {
        var info = await GetDeviceInformationAsync(ct);
        if (info == null)
            return BlePairResult.Fail("取 DeviceInformation 失败（未连接？）");

        if (info.Pairing.IsPaired)
            return new BlePairResult { Ok = true, StatusText = "AlreadyPaired（已绑定，跳过）" };

        if (!info.Pairing.CanPair)
            return BlePairResult.Fail("该设备不支持平台配对（CanPair=false）");

        if (!Enum.TryParse<DevicePairingProtectionLevel>(protection, ignoreCase: true, out var level))
            return BlePairResult.Fail($"保护级别非法：{protection}（default/none/encryption/authentication）");

        // 保护级别变化时需显式登记，否则 PairAsync(level) 会拒绝
        var ceremony = DevicePairingKinds.ProvidePin | DevicePairingKinds.ConfirmOnly;
        info.Pairing.Custom.PairingRequested += (_, a) =>
        {
            // 测试位：ConfirmOnly 直接接受；ProvidePin 场景由用户在系统弹窗输入（MITM 类）
            a.Accept(a.PairingKind == DevicePairingKinds.ProvidePin ? "000000" : null);
        };
        var result = await info.Pairing.Custom.PairAsync(ceremony, level)
            .AsTask().WaitAsync(TimeSpan.FromMinutes(2), ct);

        return new BlePairResult
        {
            Ok = result.Status == DevicePairingResultStatus.Paired,
            StatusText = result.Status.ToString()
        };
    }

    public async Task<BlePairResult> UnpairAsync(CancellationToken ct = default)
    {
        var info = await GetDeviceInformationAsync(ct);
        if (info == null)
            return BlePairResult.Fail("取 DeviceInformation 失败（未连接？）");

        if (!info.Pairing.IsPaired)
            return new BlePairResult { Ok = true, StatusText = "AlreadyUnpaired（本来就没绑）" };

        var result = await info.Pairing.UnpairAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), ct);
        return new BlePairResult
        {
            Ok = result.Status is DeviceUnpairingResultStatus.Unpaired or DeviceUnpairingResultStatus.AlreadyUnpaired,
            StatusText = result.Status.ToString()
        };
    }

    // ==================== 内部 ====================

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args) =>
        ConnectionChanged?.Invoke(this, sender.ConnectionStatus == BluetoothConnectionStatus.Connected);

    private async Task<GattCharacteristic?> FindCharacteristicAsync(string uuid, CancellationToken ct)
    {
        if (!BleUuid.TryNormalize(uuid, out var normalized))
            throw new ArgumentException($"非法 BLE UUID：{uuid}");

        var device = RequireDevice();
        if (device == null)
            return null;

        var svcResult = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(15), ct);
        if (svcResult.Status != GattCommunicationStatus.Success)
            return null;

        foreach (var svc in svcResult.Services)
        {
            _keepAlive.Add(svc);
            var chrResult = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct);
            if (chrResult.Status != GattCommunicationStatus.Success)
                continue;
            var hit = chrResult.Characteristics.FirstOrDefault(c =>
                string.Equals(c.Uuid.ToString().ToUpperInvariant(), normalized, StringComparison.Ordinal));
            if (hit != null)
            {
                _keepAlive.Add(hit);
                return hit;
            }
        }
        return null;
    }

    private async Task<DeviceInformation?> GetDeviceInformationAsync(CancellationToken ct)
    {
        var device = RequireDevice();
        if (device == null)
            return null;
        // CsWinRT 投影无 BluetoothDeviceId.AsString() → 按地址选择器反查 DeviceInformation
        var selector = BluetoothLEDevice.GetDeviceSelectorFromBluetoothAddress(device.BluetoothAddress);
        var list = await DeviceInformation.FindAllAsync(selector)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct);
        return list.FirstOrDefault();
    }

    private BluetoothLEDevice? RequireDevice()
    {
        if (_device == null)
            throw new InvalidOperationException("未连接（每条 CLI 命令内部自建连接，一般见不到此错）");
        return _device;
    }

    private async Task TeardownAsync(bool preserveEvent)
    {
        if (_device != null)
            _device.ConnectionStatusChanged -= OnConnectionStatusChanged;

        if (_session != null)
        {
            try { _session.MaintainConnection = false; } catch { }
            _session.Dispose();
            _session = null;
        }

        if (_device != null)
        {
            try { _device.Dispose(); } catch { }
            _device = null;
        }

        _keepAlive.Clear();
        await Task.CompletedTask;
    }

    private static IEnumerable<BluetoothAddressType> ResolveOrder(BleAddressType type) => type switch
    {
        BleAddressType.Public => new[] { BluetoothAddressType.Public },
        BleAddressType.Random => new[] { BluetoothAddressType.Random },
        _ => new[] { BluetoothAddressType.Random, BluetoothAddressType.Public }
    };

    private static ulong ToAddressValue(byte[] display)
    {
        ulong v = 0;
        foreach (var b in display)
            v = (v << 8) | b;
        return v;
    }

    private static byte[] ToDisplayBytes(ulong address)
    {
        var display = new byte[6];
        for (int i = 0; i < 6; i++)
            display[i] = (byte)((address >> (8 * (5 - i))) & 0xFF);
        return display;
    }

    private static byte[] ToBytes(IBuffer buffer)
    {
        var bytes = new byte[buffer.Length];
        using var reader = DataReader.FromBuffer(buffer);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static async Task<byte[]> ToBytesAsync(IBuffer buffer) => ToBytes(buffer);

    private static string PropertiesText(GattCharacteristicProperties p)
    {
        var list = new List<string>();
        if ((p & GattCharacteristicProperties.Read) != 0) list.Add("READ");
        if ((p & GattCharacteristicProperties.Write) != 0) list.Add("WRITE");
        if ((p & GattCharacteristicProperties.WriteWithoutResponse) != 0) list.Add("WRITE_NRSP");
        if ((p & GattCharacteristicProperties.Notify) != 0) list.Add("NOTIFY");
        if ((p & GattCharacteristicProperties.Indicate) != 0) list.Add("INDICATE");
        if ((p & GattCharacteristicProperties.Broadcast) != 0) list.Add("BROADCAST");
        if ((p & GattCharacteristicProperties.ExtendedProperties) != 0) list.Add("EXT");
        return string.Join(",", list);
    }
}
