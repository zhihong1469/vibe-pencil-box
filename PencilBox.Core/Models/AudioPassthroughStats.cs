// <copyright file="AudioPassthroughStats.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Models;

/// <summary>
/// 扩音直通状态
/// </summary>
public enum AudioPassthroughState
{
    /// <summary>未运行</summary>
    Idle,
    /// <summary>正在启动（解析设备/打开流）</summary>
    Starting,
    /// <summary>直通运行中</summary>
    Running,
    /// <summary>已停止（含出错收敛后的终态）</summary>
    Stopped
}

/// <summary>
/// 扩音直通启动参数
/// </summary>
public sealed class PassthroughStartOptions
{
    /// <summary>采集设备 ID（null = 系统默认录音设备）</summary>
    public string? CaptureDeviceId { get; set; }

    /// <summary>渲染设备 ID（null = 系统默认播放设备）</summary>
    public string? RenderDeviceId { get; set; }

    /// <summary>请求的采集采样率（Hz；设备不支持时回退设备默认格式）</summary>
    public int SampleRate { get; set; } = 16000;

    /// <summary>请求的采集声道数</summary>
    public int Channels { get; set; } = 1;

    /// <summary>渲染端目标缓冲（ms；越小延迟越低、欠载越敏感）</summary>
    public int RenderLatencyMs { get; set; } = 40;

    /// <summary>软件音量增益上限。设备端（UAC 麦克风）音量行程小，上位机软件增益允许拉到 24 倍补足；超限自动钳制。</summary>
    public const float MaxVolume = 24f;

    /// <summary>软件音量增益（0~<see cref="MaxVolume"/>，1=原样；&gt;1 时由 SoftLimiter 防破音）</summary>
    public float Volume { get; set; } = 1.0f;

    /// <summary>回声消除 AEC（扩音闭环回声；戴耳机监听时可关）</summary>
    public bool EnableAec { get; set; } = true;

    /// <summary>噪声抑制 ANS（含 AEC 残余抑制；链路关闭时旁路）</summary>
    public bool EnableAns { get; set; } = true;

    /// <summary>啸叫移频 AFC（破坏相位相干；有轻微音质代价，默认关）</summary>
    public bool EnableAfc { get; set; }

    /// <summary>AEC 残余抑制用自动档（内置推荐值）。false 时用下方手动值。</summary>
    public bool AecAuto { get; set; } = true;

    /// <summary>手动档：非双讲残余压制量（正值 dB，实际施加为负）</summary>
    public int AecSuppressDb { get; set; } = 40;

    /// <summary>手动档：双讲时压制量（正值 dB；压太深双讲可懂度受损）</summary>
    public int AecSuppressActiveDb { get; set; } = 15;

    /// <summary>降噪用自动档（内置推荐值）。false 时用手动值。</summary>
    public bool AnsAuto { get; set; } = true;

    /// <summary>手动档：降噪强度（正值 dB）</summary>
    public int AnsNoiseSuppressDb { get; set; } = 15;

    /// <summary>
    /// AEC 条件触发（实验特性，默认关）：参考信号静音满保持窗时旁路 AEC，
    /// 参考一响立即恢复。判据取参考信号（非麦克风，原因见 AecSilenceGate）。
    /// </summary>
    public bool EnableAecGate { get; set; }

    /// <summary>带通降噪下边：高通截止（Hz，0=关）。切机械抖动/低频传导；默认 80。</summary>
    public int HpfHz { get; set; } = 80;

    /// <summary>带通降噪上边：低通截止（Hz，0=关）。16k 处理时 8k 以上已被采样率天然滤除，默认关。</summary>
    public int LpfHz { get; set; }

    /// <summary>定点滤除（notch）中心频率列表（Hz，空=关）。挖电机/变压器类窄带单频啸声；
    /// 实测真实频率：录音 → `dsp run --script fft_analysis` 看 dominant/peaks。</summary>
    public int[] NotchCentersHz { get; set; } = [];

    /// <summary>定点滤除 Q（中心/带宽；越大坑越窄，默认 25 → 1kHz 带宽 40Hz）</summary>
    public int NotchQ { get; set; } = 25;

    /// <summary>桥接副汇渲染设备 ID（null/空=关）。处理链后级信号复投一份给虚拟声卡/第二设备，
    /// 供其他软件当"已降噪麦克风"用；写满即丢最旧，绝不反压主链（主延迟不受影响）。</summary>
    public string? BridgeDeviceId { get; set; }

    /// <summary>桥接副汇渲染缓冲（ms；非实时消费，默认放宽 150）</summary>
    public int BridgeLatencyMs { get; set; } = 150;
}

/// <summary>
/// 扩音直通运行统计（数值均为引擎侧观测：环形缓冲深度 ≈ 引擎附加延迟的活量）
/// </summary>
public sealed class AudioPassthroughStats
{
    /// <summary>当前状态</summary>
    public AudioPassthroughState State { get; set; }

    /// <summary>采集设备名</summary>
    public string CaptureDevice { get; set; } = string.Empty;

    /// <summary>渲染设备名</summary>
    public string RenderDevice { get; set; } = string.Empty;

    /// <summary>采集格式描述（如 16000Hz/16bit/1ch）</summary>
    public string CaptureFormat { get; set; } = string.Empty;

    /// <summary>渲染格式描述（渲染端点混音格式）</summary>
    public string RenderFormat { get; set; } = string.Empty;

    /// <summary>桥接副汇设备名（空=未启用）</summary>
    public string BridgeDevice { get; set; } = string.Empty;

    /// <summary>桥接副汇累计丢弃字节（消费不及时丢最旧的量）</summary>
    public long BridgeDroppedBytes { get; set; }

    /// <summary>定点滤除配置描述（如 "1000,2000Hz Q25"；空=未启用）</summary>
    public string NotchConfig { get; set; } = string.Empty;

    /// <summary>本次会话时长（秒）</summary>
    public double ElapsedSeconds { get; set; }

    /// <summary>采集回调次数</summary>
    public long CaptureCallbacks { get; set; }

    /// <summary>累计采集字节数（采集格式）</summary>
    public long CaptureBytes { get; set; }

    /// <summary>渲染拉取次数</summary>
    public long RenderPulls { get; set; }

    /// <summary>累计渲染字节数（渲染格式）</summary>
    public long RenderBytes { get; set; }

    /// <summary>环形缓冲当前深度（ms，按渲染格式折算）</summary>
    public double RingDepthMs { get; set; }

    /// <summary>环形缓冲深度均值（ms）</summary>
    public double RingDepthAvgMs { get; set; }

    /// <summary>环形缓冲深度峰值（ms）</summary>
    public double RingDepthMaxMs { get; set; }

    /// <summary>环形缓冲溢出丢弃字节数（丢最旧）</summary>
    public long DroppedBytes { get; set; }

    /// <summary>渲染欠载事件数（拉取时数据不足，补静音）</summary>
    public long UnderrunEvents { get; set; }

    /// <summary>最近一次采集电平（0~1，峰值）</summary>
    public float Level { get; set; }

    /// <summary>配置的渲染缓冲（ms）</summary>
    public int RenderLatencyMs { get; set; }

    /// <summary>软件音量增益</summary>
    public float Volume { get; set; } = 1.0f;

    /// <summary>限制器最近增益（0~1，1=未触发；持续 &lt;1 说明输出顶到天花板，继续加音量不再变大）</summary>
    public float LimiterGain { get; set; } = 1.0f;

    /// <summary>AEC 参考对齐偏移（ms；相对标称延迟的修正量，±几 ms 内属正常）</summary>
    public double AecOffsetMs { get; set; }

    /// <summary>DSP 处理链单帧最大耗时（ms；帧长 10ms，此值逼近 10 即实时性告急）</summary>
    public double DspMaxMs { get; set; }

    /// <summary>DSP 处理超帧次数（单帧耗时超过帧时长；非 0 即实时性已受损）</summary>
    public long DspOverruns { get; set; }

    /// <summary>
    /// Box 段估计延迟（ms）= 采集引擎周期(10) + 处理链附加(凑帧10 + AFC群延迟1，全关为0)
    /// + 环形深度(活值) + 渲染缓冲(设定值)。据此反推设备端剩余预算。
    /// </summary>
    public double EstimatedLatencyMs { get; set; }

    /// <summary>AEC 条件触发累计旁路时长（ms；0 = 门控未启用或参考从未静音满保持窗）</summary>
    public long AecGateBypassMs { get; set; }
}
