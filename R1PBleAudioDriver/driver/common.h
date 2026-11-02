// common.h — R1PBleAudio 用户态↔内核接口契约（唯一对齐点，两侧只认本文件）
// 立项 2026-11-02（指挥官令：自研虚拟麦克风，自有设备名）。
// v0.1 骨架：契约设计定稿可评审；DDI 迭代待 M1（VS+WDK，见上层 README）。
#ifndef _R1PBLEAUDIO_COMMON_H_
#define _R1PBLEAUDIO_COMMON_H_

// ============ 设备与接口 ============
#define R1PAUDIO_DEVICE_NAME_A   "R1PBleAudio"                 // 设备显示名（声音面板）
#define R1PAUDIO_NTNAME_W        L"\\Device\\R1PBleAudio"      // NT 设备对象名
#define R1PAUDIO_SYMNAME_W       L"\\DosDevices\\R1PBleAudio"  // 用户态符号链接

// ============ 音频规格（与软件接收器解码 PCM 同规格） ============
#define R1PAUDIO_SAMPLE_RATE   16000
#define R1PAUDIO_CHANNELS      1
#define R1PAUDIO_BITS          16
#define R1PAUDIO_FRAME_BYTES   (R1PAUDIO_CHANNELS * R1PAUDIO_BITS / 8)   // 2B
// 一块 PCM 建议 20ms（320B）对齐硬件基站 UAC 包络；环容量=约 1s 防抖
#define R1PAUDIO_RING_BYTES    (R1PAUDIO_SAMPLE_RATE * R1PAUDIO_FRAME_BYTES)  // 32000B ≈1s

// ============ IOCTL（用户态写入端 → 驱动） ============
// FILE_DEVICE_UNKNOWN(0x22)，访问任意，各方法见注
#define R1PAUDIO_IOCTL_BASE    0x2800

// 写 PCM 块：METHOD_OUT_DIRECT（缓冲=PCM 原始字节，MDL 直锁）。
// 契约：整块原子入环；环满=丢最旧腾位并累加 OverflowFrames（语义：实时流优先新鲜，
//       与 SrxBridgePump"环满丢旧"同口径）；返回后 Irp->IoStatus.Information=实际入环字节数。
#define IOCTL_R1P_WRITE_PCM    CTL_CODE(FILE_DEVICE_UNKNOWN, R1PAUDIO_IOCTL_BASE + 0, METHOD_OUT_DIRECT, FILE_ANY_ACCESS)

// 读运行态（METHOD_BUFFERED，出参 R1PAUDIO_STATS）：
#define IOCTL_R1P_GET_STATS    CTL_CODE(FILE_DEVICE_UNKNOWN, R1PAUDIO_IOCTL_BASE + 1, METHOD_BUFFERED, FILE_ANY_ACCESS)

// 复位环（清积压；PencilBox 连接断开/重开时调用）：
#define IOCTL_R1P_FLUSH        CTL_CODE(FILE_DEVICE_UNKNOWN, R1PAUDIO_IOCTL_BASE + 2, METHOD_BUFFERED, FILE_ANY_ACCESS)

// ============ 运行态统计（IOCTL_R1P_GET_STATS 出参） ============
typedef struct _R1PAUDIO_STATS {
    ULONG  Size;              // 本结构大小（防版本错配，写入端校验）
    ULONG  WrittenFrames;     // 累计入环帧数（20ms 块计）
    ULONG  ReadFrames;        // 累计被 miniport 取走帧数
    ULONG  OverflowFrames;    // 环满丢旧块数（>0=写入端供过于求）
    ULONG  UnderrunFrames;    // miniport 取数时环空补静音块数
    ULONG  RingUsedBytes;     // 当前环占用
    ULONG  RingTotalBytes;    // 环总容量
} R1PAUDIO_STATS, *PR1PAUDIO_STATS;

// ============ 波形格式声明（miniport 向 PortCls 报备的位流） ============
// 16k/1ch/16bit PCM，WAVEFORMATEX：nAvgBytesPerFrame=32000 B/s，BlockAlign=2
#define R1PAUDIO_BYTES_PER_SEC (R1PAUDIO_SAMPLE_RATE * R1PAUDIO_FRAME_BYTES)

#endif // _R1PBLEAUDIO_COMMON_H_
