// <copyright file="SessionAssembler.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.SerialScope;

/// <summary>
/// **多口 → 单一时间轴**（纯逻辑）：把各端口读线程产出的行汇到一处，保留最近 N 行供实时视图取用。
///
/// 双口并存的前提是"同一主机时钟、毫秒级"（05 §2），故这里**只按到达顺序入队**：
/// 各读线程是即收即投，到达顺序即时间顺序；<see cref="Recent"/> 再按时间戳稳定排序兜底。
/// </summary>
public sealed class SessionAssembler
{
    private readonly object _gate = new();
    private readonly Queue<LogLine> _ring = new();
    private readonly Dictionary<string, (long Lines, long Frames)> _perPort = new(StringComparer.Ordinal);
    private readonly int _capacity;
    private long _total;

    /// <param name="capacity">环形缓冲容量（只影响"最近可见"，日志落盘不受此限制）</param>
    public SessionAssembler(int capacity = 2000)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "容量必须为正");
        _capacity = capacity;
    }

    /// <summary>已接收的总行数（含已从环形缓冲挤出的）</summary>
    public long TotalCount
    {
        get { lock (_gate) return _total; }
    }

    /// <summary>某端口已接收的行数 / 帧数（CLI 进度行用）</summary>
    public (long Lines, long Frames) CountOf(string portName)
    {
        lock (_gate)
            return _perPort.TryGetValue(portName, out var c) ? c : (0, 0);
    }

    /// <summary>收行（线程安全；各端口读线程可并发调用）</summary>
    public void Add(LogLine line)
    {
        lock (_gate)
        {
            _ring.Enqueue(line);
            _total++;

            _perPort.TryGetValue(line.PortName, out var c);
            _perPort[line.PortName] = line.Source == LogSource.Frame
                ? (c.Lines + 1, c.Frames + 1)
                : (c.Lines + 1, c.Frames);

            if (_ring.Count > _capacity)
                _ring.Dequeue();
        }
    }

    /// <summary>最近 <paramref name="max"/> 行（时间升序）</summary>
    public IReadOnlyList<LogLine> Recent(int max)
    {
        lock (_gate)
        {
            var all = _ring.ToList();
            if (max > 0 && all.Count > max)
                all = all.Skip(all.Count - max).ToList();

            return all
                .OrderBy(l => l.Timestamp)
                .ToList();
        }
    }
}
