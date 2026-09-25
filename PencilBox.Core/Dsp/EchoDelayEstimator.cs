// <copyright file="EchoDelayEstimator.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Dsp;

/// <summary>
/// 回声延迟对齐估计器：AEC 的最大工程难点不是滤波器本身，而是"参考信号与麦克风回采之间的未知时变延迟"。
/// 本估计器周期性地在标称延迟附近 ±searchMs 窗口内做归一化互相关，跟踪真实回声路径延迟的漂移
/// （时钟漂移 ~±200ppm ≈ 0.2ms/s、渲染缓冲抖动等），把偏移修正量喂给参考取数位置。
/// 单线程（采集线程）使用。
/// </summary>
public sealed class EchoDelayEstimator
{
    private readonly int _searchSamples;
    private readonly int _updateEvery;
    private readonly float[] _refWindow;
    private int _framesSinceUpdate;

    /// <summary>相对标称延迟的修正量（样本，带符号；参考取数位置 = 标称 + OffsetSamples）</summary>
    public int OffsetSamples { get; private set; }

    /// <summary>最近一次更新的相关峰显著度（0~1，&lt;0.3 视为无可信峰，保持原值）</summary>
    public float LastPeakQuality { get; private set; }

    /// <param name="frameSamples">处理帧长（样本）</param>
    /// <param name="sampleRate">采样率</param>
    /// <param name="searchMs">搜索窗半径（ms）</param>
    /// <param name="updateEveryFrames">每隔多少帧更新一次（100 帧 @16k/10ms = 1s）</param>
    public EchoDelayEstimator(int frameSamples, int sampleRate, int searchMs = 10, int updateEveryFrames = 100)
    {
        if (frameSamples < 1) throw new ArgumentOutOfRangeException(nameof(frameSamples));
        _searchSamples = Math.Max(1, searchMs * sampleRate / 1000);
        _updateEvery = Math.Max(1, updateEveryFrames);
        FrameSamples = frameSamples;
        _refWindow = new float[frameSamples + 2 * _searchSamples];
    }

    public int FrameSamples { get; }
    public int SearchSamples => _searchSamples;

    /// <summary>
    /// 喂入一帧 mic 与覆盖 [标称-search, 标称+search+frame] 的参考窗口。
    /// 每 _updateEvery 帧做一次归一化互相关并更新 OffsetSamples（限幅 ±search，步进限幅防跳变）。
    /// 返回本次是否实际更新了 Offset。
    /// </summary>
    public bool Update(ReadOnlySpan<short> mic, ReadOnlySpan<short> referenceWindow)
    {
        if (referenceWindow.Length != _refWindow.Length)
            throw new ArgumentException($"参考窗口长度必须为 {_refWindow.Length}", nameof(referenceWindow));
        if (++_framesSinceUpdate < _updateEvery) return false;
        _framesSinceUpdate = 0;

        for (var i = 0; i < referenceWindow.Length; i++)
            _refWindow[i] = referenceWindow[i] / 32768f;

        // 归一化互相关（逐 lag），lag 定义：参考窗口相对 mic 的额外偏移
        var mLen = mic.Length;
        float bestR = 0, bestDen = 1e-9f;
        var bestLag = 0;
        float micEnergy = 0;
        for (var i = 0; i < mLen; i++)
        {
            var m = mic[i] / 32768f;
            micEnergy += m * m;
        }
        if (micEnergy < 1e-6f) return false;   // 近端静音，无可信峰

        for (var lag = -_searchSamples; lag <= _searchSamples; lag++)
        {
            float r = 0, refEnergy = 0;
            for (var i = 0; i < mLen; i++)
            {
                var rp = _refWindow[i + _searchSamples + lag];
                r += rp * (mic[i] / 32768f);
                refEnergy += rp * rp;
            }
            var den = MathF.Sqrt(refEnergy * micEnergy) + 1e-9f;
            var norm = r / den;
            if (norm > bestR) { bestR = norm; bestDen = den; bestLag = lag; }
        }

        LastPeakQuality = bestR;
        if (bestR < 0.3f) return false;   // 峰不显著（如双讲/无声段），不跟

        // 符号：窗口读取约定 backOffset = 标称 + Offset 时，实测峰 lag 满足 D = 标称 + Offset - lag，
        // 故修正量 = -lag。步进限幅：单次最多修 search/2，防相关假峰造成参考大跳。
        var step = Math.Clamp(-bestLag, -_searchSamples / 2, _searchSamples / 2);
        var newOffset = Math.Clamp(OffsetSamples + step, -_searchSamples, _searchSamples);
        var changed = newOffset != OffsetSamples;
        OffsetSamples = newOffset;
        return changed;
    }

    /// <summary>重置（AEC 重开/链路重连时）</summary>
    public void Reset()
    {
        OffsetSamples = 0;
        LastPeakQuality = 0;
        _framesSinceUpdate = 0;
    }
}
