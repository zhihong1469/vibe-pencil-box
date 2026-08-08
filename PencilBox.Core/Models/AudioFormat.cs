namespace PencilBox.Core.Models;

/// <summary>
/// 音频格式配置
/// </summary>
public class AudioFormat
{
    /// <summary>
    /// 采样率
    /// </summary>
    public int SampleRate { get; set; } = 16000;

    /// <summary>
    /// 位深度
    /// </summary>
    public int BitsPerSample { get; set; } = 16;

    /// <summary>
    /// 声道数（1=单声道, 2=立体声）
    /// </summary>
    public int Channels { get; set; } = 1;

    /// <summary>
    /// 编码格式
    /// </summary>
    public AudioEncoding Encoding { get; set; } = AudioEncoding.Pcm;

    /// <summary>
    /// 每帧采样数
    /// </summary>
    public int SamplesPerFrame { get; set; } = 256;

    /// <summary>
    /// 计算每秒字节数
    /// </summary>
    public int BytesPerSecond => SampleRate * BitsPerSample * Channels / 8;

    /// <summary>
    /// 计算每帧字节数
    /// </summary>
    public int BytesPerFrame => SamplesPerFrame * BitsPerSample * Channels / 8;

    /// <summary>
    /// 用于 WPF 显示的格式描述
    /// </summary>
    public string DisplayFormat => $"{SampleRate}Hz, {BitsPerSample}bit, {Channels}ch, {Encoding}";
}

/// <summary>
/// 音频编码格式
/// </summary>
public enum AudioEncoding
{
    /// <summary>
    /// PCM 原始数据
    /// </summary>
    Pcm,

    /// <summary>
    /// ADPCM 自适应差分 PCM
    /// </summary>
    Adpcm,

    /// <summary>
    /// Opus 编码
    /// </summary>
    Opus,

    /// <summary>
    /// G.711 μ-law
    /// </summary>
    G711UlaW
}

/// <summary>
/// 音频帧数据
/// </summary>
public class AudioFrame
{
    /// <summary>
    /// 时间戳（微秒）
    /// </summary>
    public long TimestampUs { get; set; }

    /// <summary>
    /// 帧序号
    /// </summary>
    public uint FrameIndex { get; set; }

    /// <summary>
    /// 音频数据
    /// </summary>
    public byte[] Data { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// 音频格式
    /// </summary>
    public AudioFormat Format { get; set; } = new();

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
