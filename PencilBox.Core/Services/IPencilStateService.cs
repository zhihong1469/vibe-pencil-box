using PencilBox.Core.Models;

namespace PencilBox.Core.Services;

/// <summary>
/// 笔端状态查询服务接口
/// 负责读取笔端电量、蓝牙连接状态、信号强度、设备信息、OTA 支持。
/// 对齐 link/host/usb/03_笔端状态查询.md
/// </summary>
public interface IPencilStateService
{
    /// <summary>状态更新事件（轮询/事件触发后）</summary>
    event EventHandler<PencilState>? StateUpdated;

    /// <summary>本地快查笔端状态（0x42，mode/batt/conn/rssi，界面轮询首选）</summary>
    Task<PencilState?> GetPencilStateAsync(CancellationToken ct = default);

    /// <summary>穿透查询笔端电量（0x41，权威值）</summary>
    Task<byte?> QueryBatteryAsync(CancellationToken ct = default);

    /// <summary>穿透查询笔端设备信息（0x40，型号/固件/协议版本）</summary>
    Task<PencilDeviceInfo?> QueryDeviceInfoAsync(CancellationToken ct = default);

    /// <summary>查询 OTA 支持（0x43）</summary>
    Task<OtaSupportInfo?> GetOtaSupportAsync(CancellationToken ct = default);

    /// <summary>当前缓存的状态快照</summary>
    PencilState? CurrentState { get; }
}
