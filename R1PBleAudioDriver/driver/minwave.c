// minwave.c — capture miniport（IMiniportWaveRT）+ WaveRT 流：从环形缓冲取数给 PortCls
// 蓝本=sysvad（Microsoft Windows-driver-samples，MIT）；改编保留注记（M1 起逐文件补全）。
// v0.1 骨架：接口形状与调用流程齐全，编译与 DDI 细节迭代待 M1（VS+WDK）。
#include "common.h"
#include "pcmring.h"

// ---- 前向：COM 接口表（PortCls 以 C++ 抽象基类暴露，C 侧用 vtable 手工对齐） ----
typedef struct _R1P_WAVE_MINIPORT   *PR1P_WAVE_MINIPORT;
typedef struct _R1P_WAVE_STREAM     *PR1P_WAVE_STREAM;

// ---- miniport：一设备一实例 ----
typedef struct _R1P_WAVE_MINIPORT {
    PVOID              UnknownOuter;      // 无聚合：指自身
    PVOID              MiniportWaveRT;    // IMiniportWaveRT vtable（M1 补全五方法）
    PDEVICE_OBJECT     DeviceObject;
    PR1P_RING          Ring;              // 共享环（adapter StartDevice 注入）
    PKSDATAFORMAT_WAVEFORMATEX Format;    // 16k/1ch/16bit（common.h 规格表）
} R1P_WAVE_MINIPORT;

// ---- 流：一渲染会话一实例（录音应用打开时 PortCls NewStream） ----
typedef struct _R1P_WAVE_STREAM {
    PVOID          UnknownOuter;
    PVOID          MiniportWaveRTStream; // IMiniportWaveRTStream vtable（M1 补全）
    PR1P_WAVE_MINIPORT Parent;
    BOOLEAN        Started;
    KSSTATE        State;
    ULONG          PositionBytes;        // 累计取走字节（GetPosition 回报）
    // WaveRT 通知：M1 选轮询模型起步（position register），事件模型后置
} R1P_WAVE_STREAM;

/* M1 迭代项（先立清单防漏）：
 * IMiniportWaveRT：
 *   GetDeviceDescription   → 填 PCDEVICE_DESCRIPTION（R1PBleAudio，Type=WaveRTDevice）
 *   Init                   → 存 DeviceObject/Ring；PcNewResourceList 消化资源表
 *   NewStream              → new R1P_WAVE_STREAM + 返回 IMiniportWaveRTStream
 *   Service                → 空（无中断模型）
 *   FreeResourcesWithMapping → M2 后按需
 * IMiniportWaveRTStream：
 *   GetFormat / SetFormat  → 只认 16k/1ch/16bit（KSDATAFORMAT_WAVEFORMATEX 校验）
 *   SetState               → KSSTATE_RUN=Started；STOP/PAUSE 置停
 *   GetClockRegister       → 骨架阶段返回 NullRegister（PortCls 兜底虚拟钟）
 *   GetPositionRegister    → 骨架阶段 NullRegister + GetPosition 回读 PositionBytes
 *   ReadNotification       → 从环 R1pRingRead 进 WaveRT buffer；环空补零 UnderrunBlocks++
 *   FreeResourcesWithMapping / GetHWLatency → 起步给安全默认
 * 计时模型：无中断；用流内每 N ms 软推进（KeSetTimer 或 ExSetTimer）驱动 ReadNotification —— M1 决策点
 */
