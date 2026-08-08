namespace PencilBox.Core.Models;

/// <summary>
/// 音频捕获统计与丢包检测数据
/// </summary>
public class AudioCaptureStats
{
    /// <summary>
    /// 总接收字节数
    /// </summary>
    public long TotalBytesReceived { get; set; }

    /// <summary>
    /// 总接收帧数
    /// </summary>
    public long TotalFramesReceived { get; set; }

    /// <summary>
    /// 根据时间推算的预期接收字节数
    /// </summary>
    public long ExpectedBytes { get; set; }

    /// <summary>
    /// 估算的丢包字节数
    /// </summary>
    public long LostBytes { get; set; }

    /// <summary>
    /// 估算的丢包率（0.0 ~ 1.0）
    /// </summary>
    public double LostRatio { get; set; }

    /// <summary>
    /// 检测到的异常间隔次数（回调间隔过大）
    /// </summary>
    public int GlitchCount { get; set; }

    /// <summary>
    /// 当前电平（0.0 ~ 1.0）
    /// </summary>
    public double CurrentLevel { get; set; }

    /// <summary>
    /// 当前采样率
    /// </summary>
    public int SampleRate { get; set; }

    /// <summary>
    /// 当前声道数
    /// </summary>
    public int Channels { get; set; }

    /// <summary>
    /// 当前位深度
    /// </summary>
    public int BitsPerSample { get; set; }

    /// <summary>
    /// 捕获持续时间（秒）
    /// </summary>
    public double DurationSeconds { get; set; }

    /// <summary>
    /// 上次数据到达时间
    /// </summary>
    public DateTime LastDataTime { get; set; }

    /// <summary>
    /// 格式化显示丢包统计
    /// </summary>
    public string Summary => $"Bytes: {TotalBytesReceived:N0} | Expected: {ExpectedBytes:N0} | Lost: {LostRatio:P1} | Glitches: {GlitchCount}";
}
