// <copyright file="ShortDelayRing.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Audio;

/// <summary>
/// short 采样延迟环（SPSC）：渲染线程写入参考信号，采集线程按"往回数 N 个样本"取对齐窗口（不消费）。
/// 溢出丢最旧（保留最新），这正确——参考只需要足够新的历史。
/// 写读各单线程；索引用 Volatile 传递，无锁。
/// </summary>
public sealed class ShortDelayRing
{
    private readonly short[] _buf;
    private readonly int _mask;
    private long _writePos;   // 累计写入样本数（单调递增）

    /// <param name="capacitySamples">容量（样本数，向上取 2 的幂）</param>
    public ShortDelayRing(int capacitySamples)
    {
        if (capacitySamples < 1) throw new ArgumentOutOfRangeException(nameof(capacitySamples));
        var cap = 1;
        while (cap < capacitySamples) cap <<= 1;
        _buf = new short[cap];
        _mask = cap - 1;
        Capacity = cap;
    }

    public int Capacity { get; }

    /// <summary>当前可回看的样本深度（写入方与读取方都可见的近似值，单调）</summary>
    public long Depth => Volatile.Read(ref _writePos);

    /// <summary>写入（仅渲染线程调用）</summary>
    public void Write(ReadOnlySpan<short> samples)
    {
        var pos = Volatile.Read(ref _writePos);
        foreach (var s in samples)
            _buf[pos++ & _mask] = s;
        Volatile.Write(ref _writePos, pos);
    }

    /// <summary>
    /// 取"最新样本往回 backOffset 个"结尾、长度为 dest.Length 的窗口（仅采集线程调用，不消费）。
    /// backOffset=0 → 最新 dest.Length 个样本。backOffset 为负、历史不足或已被覆盖返回 false。
    /// </summary>
    public bool TryReadAt(Span<short> dest, long backOffset)
    {
        if (dest.Length == 0) return true;
        if (backOffset < 0) return false;   // 未来样本不存在
        var writePos = Volatile.Read(ref _writePos);
        // 窗口最早样本的绝对位置
        var oldest = writePos - backOffset - dest.Length;
        if (oldest < 0) return false;                       // 历史不足（启动初期）
        if (writePos - oldest > Capacity) return false;     // 已被覆盖
        for (var i = 0; i < dest.Length; i++)
            dest[i] = _buf[(oldest + i) & _mask];
        return true;
    }
}
