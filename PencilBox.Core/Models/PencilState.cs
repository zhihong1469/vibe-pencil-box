// <copyright file="PencilState.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Protocol;

namespace PencilBox.Core.Models;

/// <summary>
/// 笔端状态快照（来自 GET_PENCIL_STATE 0x42 应答）
/// [status][mode][batt][conn_state][rssi]
/// </summary>
public class PencilState
{
    /// <summary>应答 status（0x00=成功）</summary>
    public byte Status { get; set; }

    /// <summary>当前模式</summary>
    public PencilMode Mode { get; set; }

    /// <summary>电量百分比（0~100，0xFF=未知）</summary>
    public byte Battery { get; set; }

    /// <summary>蓝牙连接状态</summary>
    public PencilConnectionState ConnectionState { get; set; }

    /// <summary>信号强度 RSSI（dBm，负数；0=未知）</summary>
    public sbyte Rssi { get; set; }

    /// <summary>是否已连接</summary>
    public bool IsConnected => ConnectionState == PencilConnectionState.Connected;

    /// <summary>解析 0x42 应答载荷</summary>
    public static PencilState FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5)
            return new PencilState { Status = 0x7F };

        return new PencilState
        {
            Status = data[0],
            Mode = (PencilMode)data[1],
            Battery = data[2],
            ConnectionState = (PencilConnectionState)data[3],
            Rssi = (sbyte)data[4]
        };
    }
}

/// <summary>
/// 笔端设备信息（来自 QUERY_DEVICE_INFO 0x40 应答）
/// [status][型号(2B)][固件版本(4B)][协议版本(1B)]
/// </summary>
public class PencilDeviceInfo
{
    public byte Status { get; set; }
    public ushort Model { get; set; }
    public uint FirmwareVersion { get; set; }
    public byte ProtocolVersion { get; set; }

    /// <summary>解析 0x40 应答载荷</summary>
    public static PencilDeviceInfo FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
            return new PencilDeviceInfo { Status = 0x7F };

        return new PencilDeviceInfo
        {
            Status = data[0],
            Model = (ushort)((data[1] << 8) | data[2]),
            FirmwareVersion = ((uint)data[3] << 24) | ((uint)data[4] << 16) | ((uint)data[5] << 8) | data[6],
            ProtocolVersion = data[7]
        };
    }

    public string FirmwareDisplay =>
        $"{(FirmwareVersion >> 24) & 0xFF}.{(FirmwareVersion >> 16) & 0xFF}.{(FirmwareVersion >> 8) & 0xFF}.{FirmwareVersion & 0xFF}";
}

/// <summary>
/// OTA 支持信息（来自 GET_OTA_SUPPORT 0x43 应答）
/// [status][support(0/1)][ota_channel(0=CDC 1=CustomHID)]
/// </summary>
public class OtaSupportInfo
{
    public byte Status { get; set; }
    public bool Supported { get; set; }
    public byte OtaChannel { get; set; }

    public static OtaSupportInfo FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < 3)
            return new OtaSupportInfo { Status = 0x7F };

        return new OtaSupportInfo
        {
            Status = data[0],
            Supported = data[1] == 1,
            OtaChannel = data[2]
        };
    }
}
