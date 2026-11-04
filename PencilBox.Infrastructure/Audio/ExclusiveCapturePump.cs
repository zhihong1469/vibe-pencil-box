// <copyright file="ExclusiveCapturePump.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PencilBox.Infrastructure.Audio;

/// <summary>
/// 独占模式**流式**采集泵（扩音链一键独占，2026-11-05）：t7 的 <see cref="ExclusiveCaptureEngine"/>
/// 是落盘录音件；本泵把同一套已真机验证的独占初始化配方（候选表协商 + 0x88890019 对齐重试 +
/// 周期 {10,20}ms）接到扩音主链的 DataAvailable 漏斗上——事件签名与 NAudio WasapiCapture 一致
/// （<c>EventHandler&lt;WaveInEventArgs&gt;</c>），引擎侧消费代码零改动。
/// 线程：专用 Thread（Priority=Highest）+ 事件同步读循环——async 续延掉回线程池、优先级只覆盖
/// 首段（HID 读线程同款教训）；停止=先收旗再 <c>AudioClient.Stop()</c>，读线程 200ms 窗内自退，
/// Dispose 幂等（收尾纪律=与串口/NAudio Join 坑同族：调用线程零阻塞 Join）。
/// </summary>
public sealed class ExclusiveCapturePump : IDisposable
{
    /// <summary>与 NAudio WasapiCapture.DataAvailable 同签名——扩音引擎漏斗直接挂。</summary>
    public event EventHandler<WaveInEventArgs>? DataAvailable;

    /// <summary>与 NAudio WasapiCapture.RecordingStopped 同签名（异常经 <see cref="StoppedEventArgs.Exception"/> 带出）。</summary>
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    /// <summary>独占初始化失败码：端点被占（独占与同端点任何流互斥，含自己的另一条流）</summary>
    public const int AudclntEDeviceInUse = unchecked((int)0x8889000A);

    /// <summary>协商成功的采集格式（Start 后有效）</summary>
    public WaveFormat Format { get; private set; } = null!;

    private AudioClient? _client;
    private MMDevice? _device;
    private Thread? _thread;
    private volatile bool _stopping;
    private bool _disposed;

    /// <summary>
    /// 候选协商（纯函数可测）：请求格式优先，逐个问 <paramref name="isSupported"/>(Exclusive)；
    /// 全不支持返回 null 并给出原因（报实情不硬猜——t7 B-1 口径）。
    /// </summary>
    public static WaveFormat? SelectFormat(WaveFormat requested, WaveFormat[] fallbacks,
        Func<WaveFormat, bool> isSupported, out string reason)
    {
        // 请求格式放首位；与回退表去重（重复探测无害但浪费一次 AudioClient 往返）
        var candidates = new List<WaveFormat> { requested };
        foreach (var f in fallbacks)
            if (!candidates.Any(c => c.SampleRate == f.SampleRate && c.Channels == f.Channels
                                                    && c.BitsPerSample == f.BitsPerSample))
                candidates.Add(f);

        foreach (var f in candidates)
        {
            if (isSupported(f))
            {
                reason = string.Empty;
                return f;
            }
        }
        var listed = string.Join("、", candidates.Select(c => $"{c.SampleRate}Hz/{c.BitsPerSample}bit/{c.Channels}ch"));
        reason = $"设备独占模式不支持候选格式（{listed}）——独占需设备原生暴露该格式";
        return null;
    }

    /// <summary>独占失败归因（纯函数可测）：占用/不支持/其它，落日志与 CaptureModeNote。</summary>
    public static string DescribeExclusiveFailure(Exception ex)
    {
        if (ex is COMException cex)
        {
            if (cex.HResult == AudclntEDeviceInUse)
                return $"端点被占用（独占与同端点任何流互斥，0x{cex.HResult:X8}）——先停掉其他用麦的进程/流";
            if (cex.HResult == unchecked((int)0x88890019))
                return "独占缓冲周期非对齐（0x88890019）";
        }
        return $"独占初始化失败：{ex.Message}";
    }

    /// <summary>
    /// 启动独占采集（格式须已经 <see cref="SelectFormat"/> 协商通过；Initialize 再失败=设备态竞态，
    /// 异常原样上抛由调用方回退共享）。
    /// </summary>
    public void Start(MMDevice device, WaveFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _device = device;
        _client = device.AudioClient;
        ExclusiveCaptureEngine.InitializeExclusive(_client, format);
        Format = format;

        var ev = new EventWaitHandle(false, EventResetMode.AutoReset);
        _client.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());
        var capture = _client.AudioCaptureClient;
        var blockAlign = format.BlockAlign;
        // 200ms 容量上界（与 t7 落盘件同口径）；事件回调内超出按需扩容
        var buffer = new byte[Math.Max(4096, format.SampleRate / 5 * blockAlign)];

        _stopping = false;
        _thread = new Thread(() => ReadLoop(capture, ev, buffer, blockAlign))
        {
            IsBackground = true,
            Name = "PencilBox.ExclusiveCapture",
            Priority = ThreadPriority.Highest
        };
        _client.Start();
        _thread.Start();
    }

    private void ReadLoop(AudioCaptureClient capture, EventWaitHandle ev, byte[] buffer, int blockAlign)
    {
        try
        {
            while (!_stopping)
            {
                if (!ev.WaitOne(200))
                    continue;                       // 无进料期（静音设备也可能不发事件）轮询旗标防悬挂
                while (!_stopping && capture.GetNextPacketSize() > 0)
                {
                    var ptr = capture.GetBuffer(out var frames, out _);
                    var bytes = frames * blockAlign;
                    if (bytes > 0)
                    {
                        if (bytes > buffer.Length) Array.Resize(ref buffer, bytes);
                        System.Runtime.InteropServices.Marshal.Copy(ptr, buffer, 0, bytes);
                        DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, bytes));
                    }
                    capture.ReleaseBuffer(frames);
                }
            }
        }
        catch (Exception ex)
        {
            if (!_stopping)
                RecordingStopped?.Invoke(this, new StoppedEventArgs(ex));
            return;
        }
        RecordingStopped?.Invoke(this, new StoppedEventArgs(null));
    }

    /// <summary>停流（非阻塞收口：收旗 + AudioClient.Stop，读线程 200ms 窗内自退；Dispose 幂等）。</summary>
    public void Stop()
    {
        _stopping = true;
        try { _client?.Stop(); } catch { /* 已停则忽略 */ }
        _thread?.Join(1000);                    // 仅等线程退出位，不在锁/UI 路径
        _thread = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
        try { _client?.Dispose(); } catch { /* 收尾容错 */ }
        _client = null;
        try { _device?.Dispose(); } catch { /* 收尾容错 */ }
        _device = null;
    }
}
