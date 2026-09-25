// <copyright file="PcmRingBuffer.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Audio;

/// <summary>
/// 定容 SPSC PCM 字节环形缓冲（扩音直通用）。
/// 语义：产能恒大于耗能时按"丢最旧"保新鲜（PA/喊话语义），溢出量计入 DroppedBytes；
/// 读侧数据不足时由调用方补静音并计欠载。非泛型字节面，格式转换在写入前完成。
/// </summary>
public sealed class PcmRingBuffer
{
    private readonly byte[] _buffer;
    private readonly object _lock = new();
    private int _writePos;   // 下一个写入位置
    private int _readPos;    // 下一个读取位置
    private int _count;      // 当前有效字节数

    /// <summary>缓冲容量（字节）</summary>
    public int CapacityBytes { get; }

    /// <summary>累计写入字节数</summary>
    public long TotalWrittenBytes { get; private set; }

    /// <summary>累计读出字节数</summary>
    public long TotalReadBytes { get; private set; }

    /// <summary>累计因溢出被丢弃的最旧字节数</summary>
    public long DroppedBytes { get; private set; }

    /// <summary>当前有效字节数</summary>
    public int Count { get { lock (_lock) return _count; } }

    public PcmRingBuffer(int capacityBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityBytes, 1);
        CapacityBytes = capacityBytes;
        _buffer = new byte[capacityBytes];
    }

    /// <summary>
    /// 写入数据。空间不足时丢最旧腾位，保证本次写入完整。
    /// </summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        lock (_lock)
        {
            if (data.Length > CapacityBytes)
                throw new ArgumentException($"写入 {data.Length}B 超过环形缓冲容量 {CapacityBytes}B", nameof(data));

            var overflow = data.Length + _count - CapacityBytes;
            if (overflow > 0)
            {
                _readPos = (_readPos + overflow) % CapacityBytes;
                _count -= overflow;
                DroppedBytes += overflow;
            }

            var first = Math.Min(data.Length, CapacityBytes - _writePos);
            data[..first].CopyTo(_buffer.AsSpan(_writePos));
            data[first..].CopyTo(_buffer.AsSpan(0, data.Length - first));
            _writePos = (_writePos + data.Length) % CapacityBytes;
            _count += data.Length;
            TotalWrittenBytes += data.Length;
        }
    }

    /// <summary>
    /// 读取数据（最多 dest.Length 字节），返回实际读出字节数；不足时由调用方补静音。
    /// </summary>
    public int Read(Span<byte> dest)
    {
        if (dest.IsEmpty) return 0;
        lock (_lock)
        {
            var take = Math.Min(dest.Length, _count);
            if (take == 0) return 0;

            var first = Math.Min(take, CapacityBytes - _readPos);
            _buffer.AsSpan(_readPos, first).CopyTo(dest);
            if (take > first)
                _buffer.AsSpan(0, take - first).CopyTo(dest[first..]);

            _readPos = (_readPos + take) % CapacityBytes;
            _count -= take;
            TotalReadBytes += take;
            return take;
        }
    }
}
