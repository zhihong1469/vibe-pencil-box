// <copyright file="VpCommandBuilder.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Ble.SoftReceiver;

/// <summary>
/// 下行命令组装器（对齐基站 <c>sdk_ble_vp_cmd_send</c>：CMD value = [cmd_id][param...]，≤64B，ATT Write Request）。
/// </summary>
public static class VpCommandBuilder
{
    /// <summary>组装设置语音状态命令（0x20，param=[state]）。</summary>
    public static byte[]? SetVoiceState(byte state) => Build(VpProtocol.CmdSetVoiceState, state);

    /// <summary>组装查询设备信息命令（0x40，无 param）。</summary>
    public static byte[]? QueryDeviceInfo() => Build(VpProtocol.CmdQueryDeviceInfo);

    /// <summary>组装查询电量命令（0x41，无 param）。</summary>
    public static byte[]? QueryBatt() => Build(VpProtocol.CmdQueryBatt);

    /// <summary>通用组装：[cmd_id][param...]，超长返回 null（对齐基站 -2 拒发）。</summary>
    public static byte[]? Build(byte cmdId, params ReadOnlySpan<byte> param)
    {
        if (1 + param.Length > VpProtocol.CmdMaxLen)
            return null;
        var buf = new byte[1 + param.Length];
        buf[0] = cmdId;
        param.CopyTo(buf.AsSpan(1));
        return buf;
    }
}
