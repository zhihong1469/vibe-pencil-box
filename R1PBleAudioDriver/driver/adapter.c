// adapter.c — 驱动入口 + PortCls 适配器装配 + IOCTL 设备控制分发
// 蓝本=sysvad adapter.cpp 结构（MIT）；数据通道改为 IOCTL 环形缓冲（契约=common.h）。
// v0.1 骨架：调用点/流程/分发表齐全；编译迭代待 M1。
#include "common.h"
#include "pcmring.h"

// ---- 全局：一设备一环（单端点单实例，无多卡场景） ----
static PR1P_RING g_Ring = NULL;

// ---- 1) DriverEntry：把驱动对象交给 PortCls 托管 ----
/* M1：PcInitializeAdapterDriver(DriverObject, RegistryPath, R1pAddDevice)
 *     + MajorFunction[IRP_MJ_DEVICE_CONTROL] 挂到本文件的 IOCTL 分发
 *     （PortCls 托管 PnP/Power，DeviceControl 我们自留——sysvad 无此通道，本驱动特有） */

// ---- 2) AddDevice：PcAddAdapterDevice 装配 FDO ----
/* M1：PcAddAdapterDriver 上下文里存 DeviceExtension{Ring=g_Ring}
 *     启动次序：DriverEntry 建 g_Ring（R1pRingCreate）→ AddDevice 存指针 →
 *     卸载 DriverUnload R1pRingDestroy（PAGED 语义核对放 M1） */

// ---- 3) StartDevice：注册 wave+topology 子设备 ----
/* M1：PortCls StartDevice 回调里：
 *     PcNewMiniport(driver minwave) + PcNewMiniport(driver mintopo)
 *     PcRegisterSubdevice(wave capture "R1PWave") + PcRegisterSubdevice(topology "R1PTopo)
 *     PcRegisterPhysicalConnection 桥接两 pin —— sysvad 同款四步 */

// ---- 4) IOCTL 分发（用户态写入端 → 环） ----
/* M1 落地清单（契约=common.h）：
 * IOCTL_R1P_WRITE_PCM：METHOD_OUT_DIRECT → Irp->MdlAddress 锁页映射
 *   （MmGetSystemAddressForMdlSafe）→ R1pRingWrite；环满丢旧由环内部处理
 * IOCTL_R1P_GET_STATS：METHOD_BUFFERED → R1pRingStats → OutputBuffer
 * IOCTL_R1P_FLUSH：R1pRingFlush
 * 校验：OutputBufferLength>=sizeof(R1PAUDIO_STATS)（GET_STATS）、
 *       InputBufferLength 合理上限（防灌巨块：>RING_BYTES 直接 STATUS_INVALID_BUFFER_SIZE）
 * 完成路径：Irp->IoStatus.Information/Status → IoCompleteRequest(IO_NO_INCREMENT)
 */

// ---- 5) 用户态打开路径 ----
/* 符号链接 \\.\R1PBleAudio；CreateFile 每写入端一把句柄（不支持共享写=共享模式自行约定）。
 * M3 配套测试工具 tools/r1p_writer（CLI：写 WAV 文件进环，录到声即 M3 验收） */
