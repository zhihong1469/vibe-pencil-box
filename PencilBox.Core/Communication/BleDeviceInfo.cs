namespace PencilBox.Core.Communication;

/// <summary>
/// BLE 广播扫描到的设备信息（上位机侧，用于识别/推荐 SunVote 笔端目标 MAC）。
/// 纯数据模型；显示/链路序转换见 <see cref="Ble.MacAddress"/>，识别/推荐见 <see cref="Ble.BleNaming"/>。
/// </summary>
public class BleDeviceInfo
{
    /// <summary>广播地址（显示序字节，6B，最高字节在前）</summary>
    public byte[] MacDisplayOrder { get; set; } = new byte[6];

    /// <summary>广播地址（链路序字节，6B，供 A4/裸 6B 注入给 Dongle）</summary>
    public byte[] MacLinkOrder { get; set; } = new byte[6];

    /// <summary>广播名（LocalName；可为空，此时按地址/Prefix 之外的其它条件识别）</summary>
    public string? LocalName { get; set; }

    /// <summary>信号强度（dBm）</summary>
    public int Rssi { get; set; }

    /// <summary>是否 SunVote 笔端（由名字前缀判定）</summary>
    public bool IsSunVotePen { get; set; }

    /// <summary>是否配对态广播（名字以 '#' 结尾）</summary>
    public bool IsPairingMode { get; set; }

    /// <summary>名称内嵌的 MAC 末6位 HEX（仅展示/辅助；可为空）</summary>
    public string? MacSuffix { get; set; }

    /// <summary>
    /// 广播地址类型（Public/Random；建连必须传对，否则 <c>FromBluetoothAddressAsync</c> 返回 null）。
    /// 扫描时可从广播事件取到；旧数据/未知为 <see cref="BleAddressType.Unknown"/>（建连时按 Random→Public 试探）。
    /// </summary>
    public BleAddressType AddressType { get; set; } = BleAddressType.Unknown;

    /// <summary>广播地址显示序文本，如 `AA:BB:CC:DD:EE:FF`</summary>
    public string MacDisplayText => Ble.MacAddress.FormatDisplay(MacDisplayOrder);

    /// <summary>广播地址链路序文本（空格分隔 HEX，供直接下发命令帧）</summary>
    public string MacLinkText => string.Join(" ", MacLinkOrder.Select(b => b.ToString("X2")));

    public override string ToString() =>
        $"{MacDisplayText} rssi={Rssi}dBm name=\"{LocalName}\"{(IsPairingMode ? " [PAIR]" : "")}";
}
