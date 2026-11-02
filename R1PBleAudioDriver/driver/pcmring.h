// pcmring.h — 内核环形缓冲（SPSC：IOCTL 写者=单生产者，miniport 流=单消费者）
// v0.1 骨架（本文件逻辑自洽度高，M1 编译迭代主要是类型/包含收敛）
#ifndef _R1PBLEAUDIO_PCMRING_H_
#define _R1PBLEAUDIO_PCMRING_H_

#include "common.h"

typedef struct _R1P_RING {
    PUCHAR   Buffer;            // NonPaged 环存储（R1PAUDIO_RING_BYTES）
    ULONG    Capacity;          // = R1PAUDIO_RING_BYTES
    volatile LONG WritePos;     // 生产者写指针（字节，模 Capacity）
    volatile LONG ReadPos;      // 消费者读指针（字节，模 Capacity）
    // 统计（IOCTL_R1P_GET_STATS 读）
    volatile LONG WrittenBlocks;
    volatile LONG ReadBlocks;
    volatile LONG OverflowBlocks;
    volatile LONG UnderrunBlocks;
} R1P_RING, *PR1P_RING;

NTSTATUS R1pRingCreate(_Out_ PR1P_RING* Ring);
VOID     R1pRingDestroy(_Inout_ PR1P_RING* Ring);

// 单生产者写入（IOCTL 线程）：返回实际入环字节数；满=丢最旧腾位（实时流优先新鲜）
ULONG    R1pRingWrite(_In_ PR1P_RING Ring, _In_reads_bytes_(Count) const VOID* Data, _In_ ULONG Count);

// 单消费者读取（miniport 流）：环空返回 0（调用方补静音）
ULONG    R1pRingRead(_In_ PR1P_RING Ring, _Out_writes_bytes_(Count) VOID* Data, _In_ ULONG Count);

VOID     R1pRingFlush(_In_ PR1P_RING Ring);
VOID     R1pRingStats(_In_ PR1P_RING Ring, _Out_ PR1PAUDIO_STATS Stats);

#endif // _R1PBLEAUDIO_PCMRING_H_
