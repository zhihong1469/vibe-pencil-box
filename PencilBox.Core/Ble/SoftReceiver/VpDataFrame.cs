// <copyright file="VpDataFrame.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Text;

namespace PencilBox.Core.Ble.SoftReceiver;

/// <summary>DATA 帧解析结果（一次解析一份 DATA 通知值；字段对齐 V1.2 帧结构）。</summary>
public sealed class VpDataFrame
{
    /// <summary>byte0 bit7-5：协议版本（V1.2 = 2）</summary>
    public byte Version { get; init; }

    /// <summary>byte0 bit4-0：帧类型（0x01=DATA 混装 / 0x1F=压测）</summary>
    public byte FrameType { get; init; }

    /// <summary>byte1：音频包序号（仅承载音频时递增，N=0 帧不递增；用于丢帧检测）</summary>
    public byte AudioSeq { get; init; }

    /// <summary>byte2 原始状态字节</summary>
    public byte StatusByte { get; init; }

    /// <summary>byte2 bit1-0：充电枚举（V1.3：0=未充电 1=充电中 2=充满）</summary>
    public byte ChargeState => (byte)(StatusByte & VpProtocol.StateChargeMask);

    /// <summary>byte2 bit2：状态来源（false=软件态 true=硬件三档开关）</summary>
    public bool StatusFromHardware => (StatusByte & VpProtocol.StateSrcBit) != 0;

    /// <summary>byte3：原始摘要字节</summary>
    public byte Summary { get; init; }

    /// <summary>音频帧数 N（bit1-0，0=纯状态帧）</summary>
    public int AudioFrameCount => Summary & VpProtocol.SummaryNMask;

    /// <summary>byte3 bit2：通用槽有效</summary>
    public bool CommonSlotValid => (Summary & VpProtocol.SummaryCommonBit) != 0;

    /// <summary>byte3 bit3：鼠标槽有效</summary>
    public bool MouseSlotValid => (Summary & VpProtocol.SummaryMouseBit) != 0;

    /// <summary>通用槽原始 4B（[type][len][data≤2B]；bit2=0 时全零）</summary>
    public ReadOnlyMemory<byte> CommonSlot { get; init; }

    /// <summary>通用槽事件类型（空槽 = 0）</summary>
    public byte EventType => CommonSlotValid ? CommonSlot.Span[0] : (byte)0;

    /// <summary>通用槽声明的负载长度（槽容量 ≤2B；笔端 KEY_DOWN/UP 声明 3B 时如实保留，
    /// 负载仍按槽内可用字节截取——见 <see cref="EventData"/> 注）</summary>
    public byte EventLen => CommonSlotValid ? CommonSlot.Span[1] : (byte)0;

    /// <summary>
    /// 通用槽负载数据（按声明 len 截取，上限=槽内可用 2B——4B 槽 [type][len] 之后只剩 2B；
    /// 笔端 KEY_DOWN/UP（0x11/0x12）载荷=[page][key_code][modifier] 3B，基站解析读 data[2]
    /// 已越过槽界（dongle_types.h vp_key_code_t），此处如实取到哪算哪，真机首证后再定稿）。
    /// </summary>
    public ReadOnlyMemory<byte> EventData { get; init; }

    /// <summary>鼠标槽原始 4B（[buttons][x][y][wheel]，隐含 0x14；bit3=0 时全零）</summary>
    public ReadOnlyMemory<byte> MouseSlot { get; init; }

    /// <summary>状态槽区（S=2，[id,value] 序对；未知 id 原样保留，前向兼容）</summary>
    public IReadOnlyList<(byte Id, byte Value)> StatusSlots { get; init; } = Array.Empty<(byte, byte)>();

    /// <summary>音频区原始 N×60B（N=0 时空）</summary>
    public ReadOnlyMemory<byte> AudioZone { get; init; }

    /// <summary>便捷：电量槽值（无槽/未携带时 null）</summary>
    public byte? Battery => FindSlot(VpProtocol.StateIdBatt);

    /// <summary>便捷：设备状态槽值（无槽/未携带时 null）</summary>
    public byte? DevState => FindSlot(VpProtocol.StateIdDevState);

    private byte? FindSlot(byte id)
    {
        foreach (var (sid, value) in StatusSlots)
        {
            if (sid == id)
                return value;
        }
        return null;
    }
}

/// <summary>DATA 帧解析失败原因。</summary>
public enum VpFrameError
{
    None = 0,

    /// <summary>长度不足（&lt; 18B 固定区）</summary>
    TooShort,

    /// <summary>byte0 bit7-5 版本 ≠ V1.2</summary>
    BadVersion,

    /// <summary>byte0 bit4-0 类型既非 DATA 也非压测</summary>
    BadType,

    /// <summary>总长 ≠ 18 + N×60（N 取自 byte3）</summary>
    BadLength,
}

/// <summary>DATA/RSP 帧解析器（对齐基站 <c>sdk_ble_vp_on_data/on_rsp</c> 解析序，纯函数、无状态）。</summary>
public static class VpFrameParser
{
    /// <summary>解析 DATA 通知值。失败时返回 false 并给出原因（不抛异常）。</summary>
    public static bool TryParseData(ReadOnlySpan<byte> blob, out VpDataFrame? frame, out VpFrameError error)
    {
        frame = null;
        error = VpFrameError.None;

        if (blob.Length < VpProtocol.DataFixedLen)
        {
            error = VpFrameError.TooShort;
            return false;
        }

        byte verType = blob[0];
        byte ver = (byte)((verType >> VpProtocol.VerShift) & VpProtocol.VerMask);
        byte type = (byte)(verType & VpProtocol.TypeMask);

        if (ver != VpProtocol.VerV1_2)
        {
            error = VpFrameError.BadVersion;
            return false;
        }
        if (type != VpProtocol.DataType && type != VpProtocol.DataTypeStress)
        {
            error = VpFrameError.BadType;
            return false;
        }

        byte summary = blob[VpProtocol.SummaryOff];
        int n = summary & VpProtocol.SummaryNMask;
        int expectLen = VpProtocol.AudioZoneOff + n * VpProtocol.MsbcFrameLen;
        if (blob.Length != expectLen)
        {
            // 严格等长：基站侧是 `len < expect` 拒收；PC 侧通知值按 ATT MTU 截齐，
            // 短包同样视为坏帧，长包（理论不存在）一并拒绝，防越界读。
            error = VpFrameError.BadLength;
            return false;
        }

        // 状态槽区：S=2 槽全量保留（未知 id 同样保留，前向兼容对齐基站收集逻辑）
        var slots = new (byte, byte)[VpProtocol.StatusSlotCount];
        for (int i = 0; i < VpProtocol.StatusSlotCount; i++)
        {
            int off = VpProtocol.StatusSlotOff + i * VpProtocol.StatusSlotSize;
            slots[i] = (blob[off], blob[off + 1]);
        }

        // 通用槽：[type][len][data≤2B]；负载数据按槽内可用 2B 截取（EventLen 注）
        byte evtLen = blob[VpProtocol.CommonSlotOff + 1];
        int dataAvail = Math.Min((int)evtLen, VpProtocol.CommonSlotLen - 2);
        var commonSlot = blob.Slice(VpProtocol.CommonSlotOff, VpProtocol.CommonSlotLen).ToArray();

        frame = new VpDataFrame
        {
            Version = ver,
            FrameType = type,
            AudioSeq = blob[VpProtocol.AudioSeqOff],
            StatusByte = blob[VpProtocol.StatusOff],
            Summary = summary,
            CommonSlot = commonSlot,
            EventData = commonSlot.AsMemory(2, dataAvail),
            MouseSlot = blob.Slice(VpProtocol.MouseSlotOff, VpProtocol.MouseSlotLen).ToArray(),
            StatusSlots = slots,
            AudioZone = blob.Slice(VpProtocol.AudioZoneOff, n * VpProtocol.MsbcFrameLen).ToArray(),
        };
        return true;
    }

    /// <summary>解析 RSP 通知值：[cmd_id][status][data...]。len&lt;2 视为坏帧。</summary>
    public static bool TryParseRsp(ReadOnlySpan<byte> blob, out byte cmdId, out byte status, out ReadOnlyMemory<byte> data)
    {
        cmdId = 0;
        status = 0;
        data = default;
        if (blob.Length < 2)
            return false;
        cmdId = blob[0];
        status = blob[1];
        data = blob.Slice(2).ToArray();
        return true;
    }
}

/// <summary>RSP 应答帧（<see cref="VpFrameParser.TryParseRsp"/> 的结构化形态）。</summary>
public sealed class VpResponse
{
    public byte CmdId { get; init; }
    public byte Status { get; init; }
    public ReadOnlyMemory<byte> Data { get; init; }

    /// <summary>电量应答（0x41）：data[0] = 0~100；非本命令/长度不足返回 null</summary>
    public byte? Battery =>
        CmdId == VpProtocol.CmdQueryBatt && Status == VpProtocol.RspStatusOk && Data.Length >= 1
            ? Data.Span[0]
            : null;

    /// <summary>设备信息应答（0x40）：data = model 2B + fw_ver 4B + proto_ver 1B，共 7B
    /// （基站判据 len≥9 是含 [cmd_id][status] 的整包；本处 Data 已剥前 2B，故 ≥7）。
    /// fw_ver[0..3]：前两字节主/次版本，后两字节拼 16bit 修订——与笔端 0.13.21 式
    /// 三段版本号对应。非本命令/长度不足返回 null。</summary>
    public (string Model, Version FwVer, byte ProtoVer)? DeviceInfo
    {
        get
        {
            if (CmdId != VpProtocol.CmdQueryDeviceInfo || Status != VpProtocol.RspStatusOk || Data.Length < 7)
                return null;
            var s = Data.Span;
            string model = Encoding.ASCII.GetString(s.Slice(0, 2));
            var fw = new Version(s[2], s[3], (s[4] << 8) | s[5]);
            return (model, fw, s[6]);
        }
    }
}
