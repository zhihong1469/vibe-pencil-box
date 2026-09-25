// <copyright file="IAudioPassthrough.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Models;

namespace PencilBox.Core.Services;

/// <summary>
/// 扩音直通契约：采集设备 →（零前视格式转换 + 环形缓冲）→ 渲染设备实时播出。
/// 与录音路径（IAudioCapture）并行独立，不复用其缓冲与回调处理。
/// </summary>
public interface IAudioPassthrough : IDisposable
{
    /// <summary>当前状态</summary>
    AudioPassthroughState State { get; }

    /// <summary>状态变化事件</summary>
    event EventHandler<AudioPassthroughState>? StateChanged;

    /// <summary>统计更新事件（引擎侧 ~200ms 节流）</summary>
    event EventHandler<AudioPassthroughStats>? StatsUpdated;

    /// <summary>列出可用的录音（采集）设备</summary>
    Task<IReadOnlyList<AudioDeviceInfo>> GetCaptureDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>列出可用的播放（渲染）设备</summary>
    Task<IReadOnlyList<AudioDeviceInfo>> GetRenderDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>开始直通（重复调用抛 InvalidOperationException）</summary>
    Task StartAsync(PassthroughStartOptions options, CancellationToken cancellationToken = default);

    /// <summary>停止直通（截断语义：缓冲中未播出的内容丢弃；未运行时为空操作）</summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>最近一次统计快照（停止后保留终值）</summary>
    AudioPassthroughStats GetStats();

    /// <summary>运行中切换回声消除（AEC）；未运行时仅记忆，Start 时生效</summary>
    void SetAecEnabled(bool enabled);

    /// <summary>运行中切换噪声抑制（ANS）</summary>
    void SetAnsEnabled(bool enabled);

    /// <summary>运行中切换啸叫移频（AFC）</summary>
    void SetAfcEnabled(bool enabled);

    /// <summary>
    /// AEC 残余抑制参数。auto=true 用内置推荐档（压制 40dB/双讲 15dB）；manual 时
    /// suppressDb = 非双讲残余压制量（正值 dB，内部取负）、suppressActiveDb = 双讲时压制量（保可懂度）。
    /// 运行中即时生效。
    /// </summary>
    void SetAecParams(bool auto, int suppressDb, int suppressActiveDb);

    /// <summary>
    /// 降噪（ANS）参数。auto=true 用内置推荐档（15dB）；manual 时 noiseSuppressDb = 降噪强度（正值 dB）。
    /// 运行中即时生效。
    /// </summary>
    void SetAnsParams(bool auto, int noiseSuppressDb);

    /// <summary>
    /// AEC 条件触发（实验特性，默认关）：参考信号静音满保持窗时旁路 AEC，参考一响立即恢复。
    /// 运行中即时切换。
    /// </summary>
    void SetAecGateEnabled(bool enabled);
}

/// <summary>
/// 回声消除处理器抽象（16k 单声道 PCM16 定帧）。
/// 实现在 Infrastructure（SpeexDSP native），Core 保持零平台依赖；由 DI 注入引擎。
/// </summary>
public interface IEchoProcessor : IDisposable
{
    /// <summary>处理帧长（样本）</summary>
    int FrameSamples { get; }

    /// <summary>
    /// 处理一帧：mic = 近端（含回声），reference = 与 mic 回声对齐的远端参考，output = 消回声后输出。
    /// 三个数组长度必须等于 <see cref="FrameSamples"/>。
    /// </summary>
    void Process(short[] mic, short[] reference, short[] output);

    /// <summary>仅做残余抑制/去噪（AEC 关、ANS 开时使用）；长度必须等于 <see cref="FrameSamples"/></summary>
    void ProcessReferenceOnly(short[] frame);

    /// <summary>AEC 残余抑制强度（正值 dB；非双讲压制 / 双讲压制，内部取负传给底层）</summary>
    void SetEchoSuppress(int db, int activeDb);

    /// <summary>降噪强度（正值 dB，内部取负传给底层）</summary>
    void SetNoiseSuppress(int db);

    /// <summary>重置自适应状态（延迟对齐大改/链路重连时）</summary>
    void Reset();
}
