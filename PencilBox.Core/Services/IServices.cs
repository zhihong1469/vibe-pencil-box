using PencilBox.Core.Communication;
using PencilBox.Core.Models;

namespace PencilBox.Core.Services;

/// <summary>
/// 设备管理服务接口
/// </summary>
public interface IDeviceService
{
    /// <summary>
    /// 设备列表变化事件
    /// </summary>
    event EventHandler<IReadOnlyList<HidDeviceInfo>>? DeviceListChanged;

    /// <summary>
    /// 当前设备信息变化事件
    /// </summary>
    event EventHandler<DeviceInfo?>? DeviceInfoChanged;

    /// <summary>
    /// 连接状态变化事件
    /// </summary>
    event EventHandler<bool>? ConnectionStateChanged;

    /// <summary>
    /// 扫描可用设备
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    Task<IReadOnlyList<HidDeviceInfo>> ScanDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 连接到指定设备
    /// </summary>
    /// <param name="devicePath">设备路径</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task<bool> ConnectAsync(string devicePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 断开设备
    /// </summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取 Dongle 设备信息
    /// </summary>
    Task<DeviceInfo?> GetDeviceInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取已连接的蓝牙设备列表
    /// </summary>
    Task<IReadOnlyList<ConnectedDevice>> GetConnectedDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 断开指定的蓝牙设备
    /// </summary>
    Task<bool> DisconnectBluetoothDeviceAsync(byte deviceIndex, CancellationToken cancellationToken = default);

    /// <summary>
    /// 重连指定的蓝牙设备
    /// </summary>
    Task<bool> ReconnectBluetoothDeviceAsync(byte deviceIndex, CancellationToken cancellationToken = default);

    /// <summary>
    /// 当前是否已连接
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// 当前 Dongle 设备信息
    /// </summary>
    DeviceInfo? CurrentDeviceInfo { get; }
}

/// <summary>
/// 按键宏服务接口
/// </summary>
public interface IMacroService
{
    /// <summary>
    /// 宏配置列表变化事件
    /// </summary>
    event EventHandler<IReadOnlyList<MacroConfig>>? MacroListChanged;

    /// <summary>
    /// 宏执行状态变化事件
    /// </summary>
    event EventHandler<MacroExecutionState>? ExecutionStateChanged;

    /// <summary>
    /// 获取所有宏配置
    /// </summary>
    IReadOnlyList<MacroConfig> GetAllMacros();

    /// <summary>
    /// 按 ID 获取宏配置
    /// </summary>
    MacroConfig? GetMacroById(string id);

    /// <summary>
    /// 添加宏配置
    /// </summary>
    Task AddMacroAsync(MacroConfig config, CancellationToken cancellationToken = default);

    /// <summary>
    /// 更新宏配置
    /// </summary>
    Task UpdateMacroAsync(MacroConfig config, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除宏配置
    /// </summary>
    Task DeleteMacroAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// 执行宏
    /// </summary>
    Task ExecuteMacroAsync(string macroId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 停止当前正在执行的宏
    /// </summary>
    Task StopExecutionAsync();

    /// <summary>
    /// 录制宏
    /// </summary>
    Task<MacroConfig> RecordMacroAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// 当前执行状态
    /// </summary>
    MacroExecutionState CurrentState { get; }
}

/// <summary>
/// 宏执行状态
/// </summary>
public enum MacroExecutionState
{
    /// <summary>
    /// 空闲
    /// </summary>
    Idle,

    /// <summary>
    /// 正在执行
    /// </summary>
    Running,

    /// <summary>
    /// 正在录制
    /// </summary>
    Recording,

    /// <summary>
    /// 已暂停
    /// </summary>
    Paused
}

/// <summary>
/// 音频服务接口
/// </summary>
public interface IAudioService
{
    /// <summary>
    /// 音频帧接收事件
    /// </summary>
    event EventHandler<AudioFrame>? FrameReceived;

    /// <summary>
    /// 音频捕获状态变化事件
    /// </summary>
    event EventHandler<AudioCaptureState>? CaptureStateChanged;

    /// <summary>
    /// 捕获统计信息更新事件
    /// </summary>
    event EventHandler<AudioCaptureStats>? StatsUpdated;

    /// <summary>
    /// 获取可用音频输入设备（含 USB Audio 1.0 识别）
    /// </summary>
    Task<IReadOnlyList<AudioDeviceInfo>> GetAvailableDevicesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 开始音频捕获
    /// </summary>
    Task StartCaptureAsync(AudioFormat format, CancellationToken cancellationToken = default);

    /// <summary>
    /// 从指定设备开始音频捕获
    /// </summary>
    Task StartCaptureAsync(string? deviceId, AudioFormat format,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 停止音频捕获
    /// </summary>
    Task StopCaptureAsync();

    /// <summary>
    /// 保存音频数据到文件
    /// </summary>
    Task SaveToFileAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取当前捕获的音频数据
    /// </summary>
    IReadOnlyList<AudioFrame> GetCapturedFrames();

    /// <summary>
    /// 获取当前捕获统计信息
    /// </summary>
    AudioCaptureStats GetStats();

    /// <summary>
    /// 清空已捕获的音频数据
    /// </summary>
    void ClearCapturedData();

    /// <summary>
    /// 当前捕获状态
    /// </summary>
    AudioCaptureState CurrentState { get; }
}

/// <summary>
/// 音频捕获状态
/// </summary>
public enum AudioCaptureState
{
    Idle,
    Capturing,
    Paused,
    Saving,
    Stopped,
    Recording
}
