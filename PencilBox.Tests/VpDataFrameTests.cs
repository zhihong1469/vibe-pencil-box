// <copyright file="VpDataFrameTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Ble.SoftReceiver;

namespace PencilBox.Tests;

/// <summary>统一 TX DATA/RSP 帧解析器单测（对齐基站 sdk_ble_vp.c 解析序）。</summary>
public class VpDataFrameTests
{
    private const byte VerData = (VpProtocol.VerV1_2 << 5) | VpProtocol.DataType; // 0x41

    /// <summary>N=0 纯状态帧（18B）：状态槽 + 充电枚举解析。</summary>
    [Fact]
    public void ParseData_StatusOnlyFrame()
    {
        var blob = new byte[VpProtocol.DataFixedLen];
        blob[0] = VerData;
        blob[1] = 0x37;                      // audio_seq（N=0 不递增，仅观测值）
        blob[2] = 0x02;                      // 充电枚举=2（充满）
        blob[3] = 0x00;                      // N=0，无槽
        blob[12] = VpProtocol.StateIdDevState;
        blob[13] = 0x00;                     // DEV_STATE 正常
        blob[14] = VpProtocol.StateIdBatt;
        blob[15] = 85;                       // 电量 85

        Assert.True(VpFrameParser.TryParseData(blob, out var frame, out var err));
        Assert.Equal(VpFrameError.None, err);
        Assert.Equal(VpProtocol.VerV1_2, frame!.Version);
        Assert.Equal(VpProtocol.DataType, frame.FrameType);
        Assert.Equal(0x37, frame.AudioSeq);
        Assert.Equal(0, frame.AudioFrameCount);
        Assert.False(frame.CommonSlotValid);
        Assert.False(frame.MouseSlotValid);
        Assert.Equal(2, frame.ChargeState);
        Assert.False(frame.StatusFromHardware);
        Assert.Equal((byte)85, frame.Battery);
        Assert.Equal((byte)0, frame.DevState);
        Assert.Equal(0, frame.AudioZone.Length);
    }

    /// <summary>N=1 音频帧：通用槽 KEY_ACTION + 鼠标槽 + 音频区全字段。</summary>
    [Fact]
    public void ParseData_AudioFrameWithSlots()
    {
        var blob = new byte[VpProtocol.AudioZoneOff + VpProtocol.MsbcFrameLen]; // 78B
        blob[0] = VerData;
        blob[1] = 0x10;
        blob[2] = 0x05;                      // 充电枚举=1 + src=硬件
        blob[3] = 0x01 | VpProtocol.SummaryCommonBit | VpProtocol.SummaryMouseBit; // 0x0D
        // 通用槽：KEY_ACTION [key_id=1][action=CLICK]
        blob[4] = VpProtocol.EvtKeyAction;
        blob[5] = 0x02;
        blob[6] = 0x01;
        blob[7] = VpProtocol.ActionClick;
        // 鼠标槽：[buttons][x][y][wheel]
        blob[8] = 0x01;
        blob[9] = 0x05;
        blob[10] = 0xFE;                     // s8 -2
        blob[11] = 0x00;
        // 状态槽
        blob[12] = VpProtocol.StateIdBatt;
        blob[13] = 66;
        // 音频区放一帧真机帧
        MsbcGoldenVectors.Frame1.CopyTo(blob, VpProtocol.AudioZoneOff);

        Assert.True(VpFrameParser.TryParseData(blob, out var frame, out _));
        Assert.Equal(1, frame!.AudioFrameCount);
        Assert.True(frame.CommonSlotValid);
        Assert.True(frame.MouseSlotValid);
        Assert.Equal(VpProtocol.EvtKeyAction, frame.EventType);
        Assert.Equal(2, frame.EventLen);
        Assert.Equal(new byte[] { 0x01, (byte)VpProtocol.ActionClick }, frame.EventData.ToArray());
        Assert.Equal(1, frame.MouseSlot.Span[0]);
        Assert.Equal(5, frame.MouseSlot.Span[1]);
        Assert.Equal(-2, (sbyte)frame.MouseSlot.Span[2]);
        Assert.Equal(1, frame.ChargeState);
        Assert.True(frame.StatusFromHardware);
        Assert.Equal((byte)66, frame.Battery);
        Assert.Equal(VpProtocol.MsbcFrameLen, frame.AudioZone.Length);
        Assert.Equal(VpProtocol.MsbcHeadMark, frame.AudioZone.Span[0]);
    }

    /// <summary>KEY_DOWN（0x11）声明 3B 负载越出槽容量：len 如实保留、数据按可用 2B 截取
    /// （笔端 KEY_DOWN 载荷 [page][key_code][modifier] 与 4B 槽位的越界细节，真机首证后定稿）。</summary>
    [Fact]
    public void ParseData_KeyDownLen3_ClampedToSlotCapacity()
    {
        var blob = new byte[VpProtocol.DataFixedLen];
        blob[0] = VerData;
        blob[3] = VpProtocol.SummaryCommonBit;
        blob[4] = VpProtocol.EvtKeyDown;
        blob[5] = 0x03;
        blob[6] = 0x07;                      // page
        blob[7] = 0x04;                      // key_code（modifier 在槽外，解析拿不到）

        Assert.True(VpFrameParser.TryParseData(blob, out var frame, out _));
        Assert.Equal(VpProtocol.EvtKeyDown, frame!.EventType);
        Assert.Equal(3, frame.EventLen);
        Assert.Equal(new byte[] { 0x07, 0x04 }, frame.EventData.ToArray());
    }

    /// <summary>长度守卫：短包 / 长度与 N 不符 / 坏版本 / 坏类型（固定区 = 16B，见 VpProtocol 注）。</summary>
    [Fact]
    public void ParseData_LengthAndHeaderGuards()
    {
        // 15B 不足固定区（16B）
        Assert.False(VpFrameParser.TryParseData(new byte[15], out _, out var err));
        Assert.Equal(VpFrameError.TooShort, err);

        // N=1 应为 76B（16+60），实给 75B
        var short1 = new byte[75];
        short1[0] = VerData;
        short1[3] = 0x01;
        Assert.False(VpFrameParser.TryParseData(short1, out _, out err));
        Assert.Equal(VpFrameError.BadLength, err);

        // 坏版本（V1.1 = 1）
        var badVer = new byte[VpProtocol.DataFixedLen];
        badVer[0] = (1 << 5) | VpProtocol.DataType;
        Assert.False(VpFrameParser.TryParseData(badVer, out _, out err));
        Assert.Equal(VpFrameError.BadVersion, err);

        // 坏类型（非 DATA/压测）
        var badType = new byte[VpProtocol.DataFixedLen];
        badType[0] = (VpProtocol.VerV1_2 << 5) | 0x06;
        Assert.False(VpFrameParser.TryParseData(badType, out _, out err));
        Assert.Equal(VpFrameError.BadType, err);

        // 压测帧放行（FrameType 如实上报，消费由上层裁决）
        var stress = new byte[VpProtocol.DataFixedLen];
        stress[0] = (VpProtocol.VerV1_2 << 5) | VpProtocol.DataTypeStress;
        Assert.True(VpFrameParser.TryParseData(stress, out var stressFrame, out _));
        Assert.Equal(VpProtocol.DataTypeStress, stressFrame!.FrameType);
    }

    /// <summary>N=2 音频帧长度校验（136B = 16+2×60 整包过、135B 拒；空口铁证 len=136）。</summary>
    [Fact]
    public void ParseData_N2FrameLengthCheck()
    {
        var ok = new byte[VpProtocol.AudioZoneOff + 2 * VpProtocol.MsbcFrameLen];
        ok[0] = VerData;
        ok[3] = 0x02;
        Assert.True(VpFrameParser.TryParseData(ok, out var frame, out _));
        Assert.Equal(2, frame!.AudioFrameCount);
        Assert.Equal(2 * VpProtocol.MsbcFrameLen, frame.AudioZone.Length);

        var bad = new byte[ok.Length - 1];
        ok.AsSpan(0, bad.Length).CopyTo(bad);
        bad[0] = VerData;
        bad[3] = 0x02;
        Assert.False(VpFrameParser.TryParseData(bad, out _, out var err));
        Assert.Equal(VpFrameError.BadLength, err);
    }

    // ---- RSP 解析 ----

    [Fact]
    public void ParseRsp_Battery()
    {
        Assert.True(VpFrameParser.TryParseRsp(new byte[] { 0x41, 0x00, 0x55 }, out var cmd, out var status, out var data));
        Assert.Equal(VpProtocol.CmdQueryBatt, cmd);
        Assert.Equal(VpProtocol.RspStatusOk, status);
        Assert.Equal(new byte[] { 0x55 }, data.ToArray());

        var rsp = new VpResponse { CmdId = cmd, Status = status, Data = data };
        Assert.Equal((byte)85, rsp.Battery);
        Assert.Null(rsp.DeviceInfo);
    }

    [Fact]
    public void ParseRsp_DeviceInfo()
    {
        // data = model "R1" + fw_ver [0,13,0,21] + proto 2
        var blob = new byte[] { 0x40, 0x00, (byte)'R', (byte)'1', 0x00, 0x0D, 0x00, 0x15, 0x02 };
        Assert.True(VpFrameParser.TryParseRsp(blob, out var cmd, out var status, out var data));
        Assert.Equal(VpProtocol.CmdQueryDeviceInfo, cmd);

        var rsp = new VpResponse { CmdId = cmd, Status = status, Data = data };
        var info = rsp.DeviceInfo;
        Assert.NotNull(info);
        Assert.Equal("R1", info!.Value.Model);
        Assert.Equal(new Version(0, 13, 21), info.Value.FwVer);
        Assert.Equal(2, info.Value.ProtoVer);
        Assert.Null(rsp.Battery);
    }

    [Fact]
    public void ParseRsp_TooShort_ReturnsFalse()
    {
        Assert.False(VpFrameParser.TryParseRsp(new byte[] { 0x41 }, out _, out _, out _));
        Assert.False(VpFrameParser.TryParseRsp(Array.Empty<byte>(), out _, out _, out _));
    }

    // ---- CMD 组装 ----

    [Fact]
    public void Build_SetVoiceState()
    {
        Assert.Equal(new byte[] { 0x20, 0x01 }, VpCommandBuilder.SetVoiceState(1));
        Assert.Equal(new byte[] { 0x40 }, VpCommandBuilder.QueryDeviceInfo());
        Assert.Equal(new byte[] { 0x41 }, VpCommandBuilder.QueryBatt());
    }

    [Fact]
    public void Build_Oversized_ReturnsNull()
    {
        Assert.Null(VpCommandBuilder.Build(0x40, new byte[64])); // 1+64 > 64
        Assert.NotNull(VpCommandBuilder.Build(0x40, new byte[63])); // 1+63 = 64 恰好
    }
}
