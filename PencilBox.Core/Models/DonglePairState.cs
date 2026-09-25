// <copyright file="DonglePairState.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Models;

/// <summary>
/// Dongle 配对状态（A1 GET_STATE 应答解码）。
/// 应答 Payload = [status][paired][connected][mode][MAC×6][name_len][name…]。
/// </summary>
public record DonglePairState
{
    public bool Valid { get; init; }
    public byte Paired { get; init; }
    public byte Connected { get; init; }
    public byte Mode { get; init; }
    /// <summary>链路序 6B MAC（链路序即 Dongle INFO3 存储序/下发序）</summary>
    public byte[] MacLink { get; init; } = Array.Empty<byte>();
    public byte NameLen { get; init; }
    public string Name { get; init; } = string.Empty;

    public bool IsPaired => Paired != 0;
    public bool IsConnected => Connected != 0;

    /// <summary>MAC 显示序文本（XX:XX:XX:XX:XX:XX），空为未配对</summary>
    public string MacDisplay
    {
        get
        {
            if (MacLink.Length != 6 || MacLink.All(b => b == 0) || MacLink.All(b => b == 0xFF))
                return "-";
            // 链路序 → 显示序反转
            return string.Join(":", MacLink.Reverse().Select(b => b.ToString("X2")));
        }
    }

    /// <summary>MAC 链路序文本（下发/存储序），供确认</summary>
    public string MacLinkText => MacLink.Length == 6 ? string.Join(" ", MacLink.Select(b => b.ToString("X2"))) : "-";

    public string ModeText => Mode switch
    {
        0x00 => "NORMAL",
        0x01 => "NAME配对窗",
        _ => $"0x{Mode:X2}"
    };

    public static DonglePairState Parse(byte[] payload)
    {
        // payload[0]=status；要求 status==0 且至少 1+10B
        if (payload.Length < 11 || payload[0] != 0x00)
            return new DonglePairState { Valid = false };

        var mac = payload.Skip(4).Take(6).ToArray();
        byte nameLen = payload[10];
        string name = nameLen > 0 && payload.Length >= 11 + nameLen
            ? System.Text.Encoding.ASCII.GetString(payload, 11, nameLen)
            : string.Empty;

        return new DonglePairState
        {
            Valid = true,
            Paired = payload[1],
            Connected = payload[2],
            Mode = payload[3],
            MacLink = mac,
            NameLen = nameLen,
            Name = name
        };
    }
}
