// pcmring.c — 内核环形缓冲实现
// SPSC 前提：写者=IOCTL 分发（可多线程并发到达→用 spinlock 串行化写侧，读侧独占于
// miniport 流线程）。跨生产者/消费者只依赖各自指针的 volatile 单向推进 + 字节拷贝，
// 不需要锁住整个环（性能足够：16k×2B=32KB/s，比任何锁竞争都小 3 个数量级）。
// v0.1 骨架：编译迭代待 M1（VS+WDK）。
#include "pcmring.h"

#pragma alloc_text(PAGE, R1pRingCreate)
#pragma alloc_text(PAGE, R1pRingDestroy)

NTSTATUS R1pRingCreate(_Out_ PR1P_RING* Ring)
{
    PAGED_CODE();
    *Ring = NULL;
    PR1P_RING r = (PR1P_RING)ExAllocatePool2(POOL_FLAG_NON_PAGED, sizeof(R1P_RING), 'P1PR');
    if (r == NULL)
        return STATUS_INSUFFICIENT_RESOURCES;
    RtlZeroMemory(r, sizeof(R1P_RING));
    r->Capacity = R1PAUDIO_RING_BYTES;
    r->Buffer = (PUCHAR)ExAllocatePool2(POOL_FLAG_NON_PAGED, r->Capacity, 'B1PR');
    if (r->Buffer == NULL) {
        ExFreePoolWithTag(r, 'P1PR');
        return STATUS_INSUFFICIENT_RESOURCES;
    }
    RtlZeroMemory(r->Buffer, r->Capacity);
    *Ring = r;
    return STATUS_SUCCESS;
}

VOID R1pRingDestroy(_Inout_ PR1P_RING* Ring)
{
    PAGED_CODE();
    PR1P_RING r = *Ring;
    if (r != NULL) {
        if (r->Buffer != NULL)
            ExFreePoolWithTag(r->Buffer, 'B1PR');
        ExFreePoolWithTag(r, 'P1PR');
    }
    *Ring = NULL;
}

static ULONG RingUsedBytes(_In_ PR1P_RING r)
{
    LONG w = *(volatile LONG*)&r->WritePos;
    LONG rd = *(volatile LONG*)&r->ReadPos;
    LONG used = w - rd;
    if (used < 0)
        used += (LONG)r->Capacity;   // 环绕
    return (ULONG)used;
}

ULONG R1pRingWrite(_In_ PR1P_RING r, _In_reads_bytes_(Count) const VOID* Data, _In_ ULONG Count)
{
    if (Count == 0)
        return 0;

    /* M1 迭代项（v0.1 骨架到此为止，先立流程再填实现）：
     * ① 写侧串行化：R1P_RING 增加 KSPIN_LOCK WriteLock；IOCTL 可 DISPATCH 级到达，
     *    用 KeAcquireSpinLock/KeReleaseSpinLock（不假设 IRQL）
     * ② 环满判定：Count > Capacity - UsedBytes 时按"丢最旧"先推 ReadPos 腾位，OverflowBlocks++
     * ③ 拷贝：RtlCopyMemory 两段（尾段/环绕头段）
     * ④ 收尾：MemoryBarrier() 后推进 WritePos；WrittenBlocks++（按 20ms=320B 块粒度）
     * ⑤ 返回实际入环字节数（=Count，腾位后必足量） */
    (VOID)r; (VOID)Data; (VOID)Count;
    return 0;
}

ULONG R1pRingRead(_In_ PR1P_RING r, _Out_writes_bytes_(Count) VOID* Data, _In_ ULONG Count)
{
    // v0.1 骨架：M1 落地——与 Write 对偶；环空返回 0 且 UnderrunBlocks++（调用方补静音）
    (VOID)r; (VOID)Data; (VOID)Count;
    return 0;
}

VOID R1pRingFlush(_In_ PR1P_RING r)
{
    r->WritePos = 0;
    r->ReadPos = 0;
}

VOID R1pRingStats(_In_ PR1P_RING r, _Out_ PR1PAUDIO_STATS s)
{
    s->Size = sizeof(R1PAUDIO_STATS);
    s->WrittenFrames = (ULONG)r->WrittenBlocks;
    s->ReadFrames = (ULONG)r->ReadBlocks;
    s->OverflowFrames = (ULONG)r->OverflowBlocks;
    s->UnderrunFrames = (ULONG)r->UnderrunBlocks;
    s->RingUsedBytes = RingUsedBytes(r);
    s->RingTotalBytes = r->Capacity;
}
