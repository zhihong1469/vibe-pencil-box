namespace PencilBox.Core.Audio;

/// <summary>
/// 凑帧器：把变长字节块累积成定长帧（供 AEC/ANS 等按定帧处理的 DSP 消费）。
/// 不足一帧的残料留到下次，附加延迟 ≤1 帧。底层复用 PcmRingBuffer（SPSC）。
/// </summary>
public sealed class FrameStitcher
{
    private readonly PcmRingBuffer _ring;
    private readonly byte[] _frame;

    public FrameStitcher(int frameBytes)
    {
        if (frameBytes < 1) throw new ArgumentOutOfRangeException(nameof(frameBytes));
        _frame = new byte[frameBytes];
        FrameBytes = frameBytes;
        // 容量给足 100ms@16k 单声道 16bit 的余量，溢出丢最旧即可
        _ring = new PcmRingBuffer(Math.Max(frameBytes * 8, 3200));
    }

    public int FrameBytes { get; }

    /// <summary>喂入任意长度数据（采集线程调用）</summary>
    public void Feed(ReadOnlySpan<byte> data) => _ring.Write(data);

    /// <summary>取出一帧（凑齐返回 true）；无帧或环内残料异常时返回 false</summary>
    public bool TryReadFrame(Span<byte> frame)
    {
        if (frame.Length != FrameBytes) throw new ArgumentException($"帧长必须为 {FrameBytes}B", nameof(frame));
        return _ring.Count >= FrameBytes && _ring.Read(frame) == FrameBytes;
    }

    /// <summary>丢弃残料（开关切换/重置时用，避免旧数据串味）</summary>
    public void Reset() => _ring.Read(new byte[_ring.Count]);
}
