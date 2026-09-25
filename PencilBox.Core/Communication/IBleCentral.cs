// <copyright file="IBleCentral.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Communication;

/// <summary>BLE 地址类型（GAP）。建连必须与广播一致，否则平台侧找不到设备。</summary>
public enum BleAddressType
{
    /// <summary>未知（建连时按 Random→Public 依次试探）</summary>
    Unknown = 0,
    /// <summary>公有地址</summary>
    Public = 1,
    /// <summary>随机地址（Static/Private Resolvable 等，笔端常用）</summary>
    Random = 2
}

/// <summary>建连请求（GAP Connect）。</summary>
public sealed record BleConnectRequest
{
    /// <summary>对端地址（显示序 `AA:BB:CC:DD:EE:FF`）</summary>
    public required string MacDisplay { get; init; }

    /// <summary>地址类型；<see cref="BleAddressType.Unknown"/> 时由实现侧试探</summary>
    public BleAddressType AddressType { get; init; } = BleAddressType.Unknown;

    /// <summary>建连超时（毫秒）</summary>
    public int TimeoutMs { get; init; } = 10_000;

    /// <summary>是否让平台维持连接（断连后自动回连，观测笔端回连行为用）</summary>
    public bool MaintainConnection { get; init; }
}

/// <summary>建连结果。</summary>
public sealed class BleConnectResult
{
    public bool Ok { get; init; }
    /// <summary>对端 GATT GAP 设备名（0x2A00 的平台缓存；扫描名不可得时的兜底）</summary>
    public string? Name { get; init; }
    public string? MacDisplay { get; init; }
    /// <summary>协商 MTU（ATT_MAX_PDU；0=平台未报告）</summary>
    public int Mtu { get; init; }
    /// <summary>建连耗时（毫秒，从发起到平台报告已连接）</summary>
    public int ConnectMs { get; init; }
    public string? Error { get; init; }

    public static BleConnectResult Fail(string error) => new() { Ok = false, Error = error };
}

/// <summary>GATT 特征信息（服务发现所得，PC 侧"客户端实搜"——对帐 V1/V3 的素材）。</summary>
public sealed class BleGattCharacteristicInfo
{
    public string ServiceUuid { get; init; } = "";
    /// <summary>特征归属服务的属性句柄</summary>
    public ushort ServiceHandle { get; init; }
    public string Uuid { get; init; } = "";
    /// <summary>特征值句柄（ATT handle；与基站端实搜 handle 可直接对帐）</summary>
    public ushort Handle { get; init; }
    /// <summary>属性位（GATT properties 原始值）</summary>
    public int Properties { get; init; }
    /// <summary>属性可读文本（READ/WRITE/WRITE_NRSP/NOTIFY/INDICATE，逗号分隔）</summary>
    public string PropertiesText { get; init; } = "";
}

/// <summary>GATT 服务信息。</summary>
public sealed class BleGattServiceInfo
{
    public string Uuid { get; init; } = "";
    public ushort Handle { get; init; }
    public IReadOnlyList<BleGattCharacteristicInfo> Characteristics { get; init; } =
        Array.Empty<BleGattCharacteristicInfo>();
}

/// <summary>服务发现（全表枚举）结果。</summary>
public sealed class BleDiscoveryResult
{
    public bool Ok { get; init; }
    public IReadOnlyList<BleGattServiceInfo> Services { get; init; } =
        Array.Empty<BleGattServiceInfo>();
    /// <summary>枚举耗时（毫秒）</summary>
    public int ElapsedMs { get; init; }
    public string? Error { get; init; }

    public int CharacteristicCount => Services.Sum(s => s.Characteristics.Count);

    public static BleDiscoveryResult Fail(string error) => new() { Ok = false, Error = error };
}

/// <summary>读值结果。</summary>
public sealed class BleValueResult
{
    public bool Ok { get; init; }
    public byte[] Data { get; init; } = Array.Empty<byte>();
    public string? Error { get; init; }

    public string HexText => string.Join(" ", Data.Select(b => b.ToString("X2")));

    /// <summary>ASCII 预览（可打印字符保留，其余以 `.` 占位）——设备名/版本类特征直读友好。</summary>
    public string AsciiPreview =>
        new(Data.Select(b => b is >= 0x20 and < 0x7F ? (char)b : '.').ToArray());

    public static BleValueResult Fail(string error) => new() { Ok = false, Error = error };
}

/// <summary>通用操作结果（写/订阅）。</summary>
public sealed class BleOpResult
{
    public bool Ok { get; init; }
    /// <summary>订阅期间收到的通知条数（写操作恒 0）</summary>
    public int Notifications { get; init; }
    public string? Error { get; init; }

    public static BleOpResult Fail(string error) => new() { Ok = false, Error = error };
}

/// <summary>配对（SMP）/解除配对结果。</summary>
public sealed class BlePairResult
{
    public bool Ok { get; init; }
    /// <summary>平台返回的状态文本（原样透出，不做翻译——排障以平台原文为准）</summary>
    public string? StatusText { get; init; }
    public string? Error { get; init; }

    public static BlePairResult Fail(string error) => new() { Ok = false, Error = error };
}

/// <summary>
/// BLE 中心设备（Central / GATT Client）操作接口——上位机"类基站"测试位。
/// 解耦设计同 <see cref="IBleScanner"/>：接口在 Core（纯逻辑），WinRT 实现在 Infrastructure；
/// <see cref="PencilBox.Core.Ble.BleStressRunner"/>（反复重连/配对压测编排）只依赖本接口，可用假件单测。
/// 定位：**测试通道**（GAP/SMP/GATT 稳定性验证），不替代基站的业务功能。
/// </summary>
public interface IBleCentral
{
    /// <summary>当前是否已连接（进程内状态；CLI 每条命令自建自拆连接）</summary>
    bool IsConnected { get; }

    /// <summary>当前连接的对端地址（显示序；未连接为 null）</summary>
    string? ConnectedMacDisplay { get; }

    /// <summary>连接状态变化（true=已连 / false=断开）——`ble watch` 观测断连-回连时间线的依据</summary>
    event EventHandler<bool>? ConnectionChanged;

    /// <summary>建连（GAP Connect）。</summary>
    Task<BleConnectResult> ConnectAsync(BleConnectRequest request, CancellationToken ct = default);

    /// <summary>主动断连。</summary>
    Task DisconnectAsync();

    /// <summary>
    /// GATT 全表枚举（服务→特征→属性/句柄）。PC 侧唯一可作证"笔端实际注册面"的手段，
    /// 对应基站 V1 全表对帐（蓝牙服务议题 01 §4）。
    /// </summary>
    /// <param name="uncached">true=绕过系统缓存直读设备（改固件后必开，防读到旧 GATT 表）</param>
    Task<BleDiscoveryResult> DiscoverAsync(bool uncached = true, CancellationToken ct = default);

    /// <summary>读特征值（GATT Read）。</summary>
    Task<BleValueResult> ReadAsync(string uuid, CancellationToken ct = default);

    /// <summary>写特征值（GATT Write；<paramref name="withoutResponse"/>=true 走 Write Without Response）。</summary>
    Task<BleOpResult> WriteAsync(string uuid, byte[] data, bool withoutResponse = false,
        CancellationToken ct = default);

    /// <summary>
    /// 订阅通知（写 CCCD）并收流 <paramref name="duration"/> 后自动退订；
    /// 每条通知经 <paramref name="onHex"/>（HEX 文本）回调。
    /// </summary>
    Task<BleOpResult> SubscribeAsync(string uuid, TimeSpan duration, Action<string>? onHex,
        CancellationToken ct = default);

    /// <summary>
    /// 触发 SMP 配对/绑定。protection：`default`/`none`/`encryption`/`authentication`
    /// （MITM 类需要交互确认，CLI 会提示）。
    /// </summary>
    Task<BlePairResult> PairAsync(string protection = "encryption", CancellationToken ct = default);

    /// <summary>解除配对/绑定（清 bond）。</summary>
    Task<BlePairResult> UnpairAsync(CancellationToken ct = default);
}
