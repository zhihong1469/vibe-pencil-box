// mintopo.c — 拓扑 miniport（IMiniportTopology）：声明"录音"端点与音量/静音节点
// v0.1 骨架：让声音面板出现 R1PBleAudio 录制设备的拓扑描述；节点功能迭代待 M1/M2。
#include "common.h"

/* M1 迭代项：
 * - PCPIN_DESCRIPTOR：1 个 KSNODETYPE_MICROPHONE 录音源引脚（bridge 到 wave pin 0）
 * - PCPROPERTY_ITEM：KSPROPERTY_AUDIO_VOLUMELEVEL（默认 0dB 直通）+ MUTE（默认不静音）
 *   ——起步可只挂"直通"无节点（sysvad SimpleAudioShape 同款），M3 再加属性
 * - 数据交叉：拓扑 pin ↔ wave miniport pin 的桥接由 PcRegisterSubdevice 完成（见 adapter.c）
 * - GetDescription 返回 topology table + interface GUID（IMiniportTopology）
 */
