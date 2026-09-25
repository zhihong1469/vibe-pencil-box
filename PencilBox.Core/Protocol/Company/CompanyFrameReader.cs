// <copyright file="CompanyFrameReader.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Protocol.Company;

/// <summary>
/// 串口（变长）字节流 → 逻辑帧 的**装配器**（纯逻辑，可离线单测）。
///
/// 流程（骨架 §4.1 串口形态 / 上位机 `01 §⑤`）：找 `F5` → `AA` → `AA` → 取 `LEN` → 收满 `LEN+4` 出一帧；
/// 帧内 `BASE_ID`/`LEN` 非法即**重同步**（丢弃当前 `F5` 继续找下一个），不把首字节当帧头丢。
///
/// 时间维度（帧间隔/超时断帧）由调用方负责：超过帧间隔仍未收满时调用 <see cref="Reset"/> 重新同步。
/// </summary>
public sealed class CompanyFrameReader
{
    private readonly List<byte> _buffer = new();
    private readonly int _maxBufferLength;

    public CompanyFrameReader(int maxBufferLength = 512)
    {
        _maxBufferLength = maxBufferLength;
    }

    /// <summary>缓冲区中已收但尚未成帧的字节数（> 0 表示"帧收了一半"）</summary>
    public int PendingLength => _buffer.Count;

    /// <summary>丢弃半帧缓冲，回到"找 F5"状态（超时断帧兜底）</summary>
    public void Reset() => _buffer.Clear();

    /// <summary>
    /// 追加一段字节流，返回本次新装配出的逻辑帧（可能 0 个、也可能多个）。
    /// </summary>
    public IReadOnlyList<byte[]> Append(ReadOnlySpan<byte> chunk)
    {
        foreach (var b in chunk)
            _buffer.Add(b);

        var frames = new List<byte[]>();

        while (true)
        {
            int head = IndexOfHead();
            if (head < 0)
            {
                // 无完整帧头：最多保留 2 字节（可能是被切断的 F5 AA），其余按垃圾丢弃
                if (_buffer.Count > 2)
                    _buffer.RemoveRange(0, _buffer.Count - 2);
                break;
            }

            if (head > 0)
                _buffer.RemoveRange(0, head);   // 丢弃帧头之前的垃圾

            if (_buffer.Count < CompanyFrame.HeaderLength)
                break;                          // 还差 LEN 字节

            int len = _buffer[3];
            if (len < 4)
            {
                _buffer.RemoveAt(0);            // LEN 非法 → 丢当前 F5，重新找下一个帧头
                continue;
            }

            int total = CompanyFrame.HeaderLength + len;
            if (_buffer.Count < total)
                break;                          // 帧未收满，等后续字节

            var frame = new byte[total];
            _buffer.CopyTo(0, frame, 0, total);
            _buffer.RemoveRange(0, total);

            if (CompanyFrame.TryParse(frame, out _, out _, out _))
                frames.Add(frame);
            // 解析失败（如 BASE_ID ≠ 0x01）→ 丢弃该帧、继续同步（不回复错误）
        }

        // 兜底：对方持续灌垃圾或 LEN 被误读时，缓冲不应无限增长
        if (_buffer.Count > _maxBufferLength)
            _buffer.Clear();

        return frames;
    }

    private int IndexOfHead()
    {
        for (int i = 0; i + 2 < _buffer.Count; i++)
        {
            if (_buffer[i] == CompanyFrame.Head0 &&
                _buffer[i + 1] == CompanyFrame.Head1 &&
                _buffer[i + 2] == CompanyFrame.Head2)
                return i;
        }
        return -1;
    }
}
