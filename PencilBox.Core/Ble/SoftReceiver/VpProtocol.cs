// <copyright file="VpProtocol.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Ble.SoftReceiver;

/// <summary>
/// 笔端统一 TX 协议常量（V1.2 帧结构），软件接收器（形态③）单源字典。
///
/// 权威出处（只读对齐，不在此复制推导）：
/// - 基站侧 <c>jl_dongle/apps/spp_and_le/examples/dongle/core/vp_protocol.h</c>（V1.2）
/// - 基站侧解析参考 <c>dongle/sdk/sdk_ble_vp.c</c>（sdk_ble_vp_on_data / on_rsp / cmd_send）
/// - 《01_统一TX协议规范.md》§15
///
/// ⚠️ 字节序铁律（B9 事故）：UUID 一律用**打印式（显示序）**字符串，PC 侧走
/// <see cref="BleUuid.TryNormalize"/> 字符串比对，不做字节重组。
/// </summary>
public static class VpProtocol
{
    // ---- 品牌族 svr1 UUID（打印式，末字节 NN 见注释） ----

    /// <summary>svr1 服务 UUID（…0011）</summary>
    public const string SvcUuid = "73767231-0000-4000-8000-000000000011";

    /// <summary>DATA 特征 UUID（…0012，Notify ≤244B，语音/事件/状态混装）</summary>
    public const string DataUuid = "73767231-0000-4000-8000-000000000012";

    /// <summary>CMD 特征 UUID（…0013，Write，下行命令 ≤64B）</summary>
    public const string CmdUuid = "73767231-0000-4000-8000-000000000013";

    /// <summary>RSP 特征 UUID（…0014，Notify 64B，命令应答）</summary>
    public const string RspUuid = "73767231-0000-4000-8000-000000000014";

    // ---- DATA 帧头 ----

    /// <summary>帧类型 = DATA 混装帧（byte0 bit4-0）</summary>
    public const byte DataType = 0x01;

    /// <summary>帧类型 = 底层吞吐压测帧（保留值，软件接收器不消费）</summary>
    public const byte DataTypeStress = 0x1F;

    /// <summary>协议版本 V1.2（byte0 bit7-5）</summary>
    public const byte VerV1_2 = 2;

    public const int VerShift = 5;
    public const int VerMask = 0x07;
    public const int TypeMask = 0x1F;

    // ---- DATA 车头偏移（4B 固定） ----

    public const int HeaderLen = 4;
    public const int AudioSeqOff = 1;
    public const int StatusOff = 2;
    public const int SummaryOff = 3;

    // ---- byte3 标志（V1.2 C1 标志化） ----

    public const int SummaryNMask = 0x03;
    public const byte SummaryCommonBit = 1 << 2;
    public const byte SummaryMouseBit = 1 << 3;

    // ---- byte2 状态字节 ----

    /// <summary>bit1-0 = 充电枚举（V1.3 勘误：0=未充电 1=充电中 2=充满；原 mode 语义退役归 0x16 事件）</summary>
    public const int StateChargeMask = 0x03;

    /// <summary>bit2 = 状态来源（0=软件态 1=硬件三档开关）</summary>
    public const byte StateSrcBit = 1 << 2;

    // ---- 事件区（8B 固定：通用槽 4B + 鼠标槽 4B） ----

    public const int EventZoneOff = HeaderLen;
    public const int EventZoneLen = 8;
    public const int CommonSlotOff = EventZoneOff;
    public const int CommonSlotLen = 4;
    public const int MouseSlotOff = EventZoneOff + CommonSlotLen;
    public const int MouseSlotLen = 4;

    // ---- 状态槽区（S=2，每槽 2B [id][value]，随包固定携带） ----

    public const int StatusSlotCount = 2;
    public const int StatusSlotSize = 2;
    public const int StatusSlotOff = HeaderLen + EventZoneLen;

    /// <summary>笔端设备状态槽 id（0=正常/1=休眠/升级中等）</summary>
    public const byte StateIdDevState = 0x01;

    /// <summary>电池电量槽 id（0~100）</summary>
    public const byte StateIdBatt = 0x02;

    // ---- 音频区（mSBC 帧 60B 定长） ----

    /// <summary>mSBC 带包装定长 60B（编解码契约，不可改动）</summary>
    public const int MsbcFrameLen = 60;

    /// <summary>mSBC 裸帧 57B（去包装后）</summary>
    public const int MsbcPayloadLen = 57;

    /// <summary>包装帧头标记 data[0]</summary>
    public const byte MsbcHeadMark = 0x01;

    /// <summary>包装帧尾标记 data[59]</summary>
    public const byte MsbcTailMark = 0x00;

    /// <summary>音频区偏移 = 4 + 8 + 4 = 16。
    /// ⚠️ 基站 vp_protocol.h 的 VP_AUDIO_ZONE_OFF 行尾注释"/* 18 */"是 0x03 空鼠槽
    /// 移除（S=3→2，2026-09-03）前的陈尸——宏实际计算 4+8+4=16；空口铁证：
    /// V1.2 真机日志 N=0 帧 len=16、N=2 帧 len=136（16+2×60）。</summary>
    public const int AudioZoneOff = HeaderLen + EventZoneLen + StatusSlotCount * StatusSlotSize;

    /// <summary>N=0 纯状态帧固定长度 = 16B（同上，勿信"18"陈旧注释）</summary>
    public const int DataFixedLen = AudioZoneOff;

    // ---- 通用槽事件类型（槽内 [type][len][data]） ----

    /// <summary>按键按下（编码）——软件接收器形态</summary>
    public const byte EvtKeyDown = 0x11;

    /// <summary>按键抬起（编码）——软件接收器形态</summary>
    public const byte EvtKeyUp = 0x12;

    /// <summary>按键动作（语义，[key_id][action] 2B）——硬件 Dongle 形态</summary>
    public const byte EvtKeyAction = 0x13;

    /// <summary>鼠标（融合位移；V1.2 起隐含走鼠标槽不占通用槽）</summary>
    public const byte EvtMouse = 0x14;

    /// <summary>状态同步（负载空，状态槽区随包携带）</summary>
    public const byte EvtStateChanged = 0x15;

    /// <summary>档位切换（负载 [档位] 1B；收到即上报，不做防重）</summary>
    public const byte EvtSwitchChanged = 0x16;

    /// <summary>P2 带内时间戳（测量期专用，[lo][hi] 2B 小端 32kHz tick）</summary>
    public const byte EvtP2Tick = 0x17;

    /// <summary>KEY_ACTION data = [key_id][action] 中的动作：单击</summary>
    public const byte ActionClick = 0x01;

    /// <summary>动作：双击</summary>
    public const byte ActionDouble = 0x02;

    /// <summary>动作：长按</summary>
    public const byte ActionLongPress = 0x03;

    /// <summary>动作：长按释放</summary>
    public const byte ActionLongRelease = 0x04;

    // ---- 下行命令 / 应答 ----

    /// <summary>CMD：设置语音状态（param=[state] 1B）</summary>
    public const byte CmdSetVoiceState = 0x20;

    /// <summary>CMD：查询设备信息（无 param；应答 data = model[2]+fw_ver[4]+proto_ver，总长 ≥9）</summary>
    public const byte CmdQueryDeviceInfo = 0x40;

    /// <summary>CMD：查询电量（无 param；应答 data[0] = 0~100）</summary>
    public const byte CmdQueryBatt = 0x41;

    /// <summary>RSP 应答状态：OK</summary>
    public const byte RspStatusOk = 0x00;

    /// <summary>CMD 特征最大写入长度（cmd_id + param）</summary>
    public const int CmdMaxLen = 64;
}
