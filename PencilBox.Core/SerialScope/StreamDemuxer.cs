// <copyright file="StreamDemuxer.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Text;
using System.Text.RegularExpressions;
using PencilBox.Core.Protocol.Company;

namespace PencilBox.Core.SerialScope;

/// <summary>字节流拆分后的两类事件</summary>
public enum StreamEventKind
{
    /// <summary>一整行设备文本日志（已清 `\r` 与 ANSI 转义）</summary>
    TextLine,

    /// <summary>一帧完整的 F5 逻辑帧（**未补零**）</summary>
    Frame
}

/// <summary>拆分事件：<see cref="Text"/> 与 <see cref="Frame"/> 按 <see cref="Kind"/> 二选一有效</summary>
public sealed record StreamEvent(StreamEventKind Kind, string? Text = null, byte[]? Frame = null);

/// <summary>
/// **字节流 → （文本行 | F5 逻辑帧）** 拆分器（纯逻辑，可离线单测）。
///
/// 为什么必须有它（而不是"直接按 `\n` 切行"）：F5 帧是**二进制**，帧内的 `DATA48` 里出现 `0x0A` 是常态，
/// 按 `\n` 切行会把一帧劈成多段垃圾文本；反之按帧切又会吞掉设备文本日志。
/// 故先按 `F5 AA AA` 找帧头、收满 `LEN+4` 出一帧，帧头之前的字节一律当文本走 —— **两类都能拿到**。
///
/// 定帧口径与既有 <see cref="CompanyFrameReader"/> 完全一致（`LEN` 是唯一定帧字段；
/// `LEN &lt; 4` 或 `BASE_ID ≠ 0x01` ⇒ 丢帧重同步），不重复发明。
/// </summary>
public sealed partial class StreamDemuxer
{
    /// <summary>未成帧字节的上限（对方持续灌垃圾时兜底，避免无界增长）</summary>
    private const int MaxPendingBytes = 4096;

    private readonly List<byte> _pending = new();      // 未分类：文本尾巴 + 可能的帧头/半帧
    private readonly List<byte> _lineBuffer = new();   // 当前文本行的字节累积

    /// <summary>追加一段字节流，返回本次新产生的事件（可能 0 个、也可能多个）</summary>
    public IReadOnlyList<StreamEvent> Append(ReadOnlySpan<byte> chunk)
    {
        var events = new List<StreamEvent>();

        foreach (var b in chunk)
            _pending.Add(b);

        while (true)
        {
            int head = IndexOfHead();
            if (head < 0)
            {
                // 无帧头：只把"可能是被切断的帧头"的尾巴留下（最多 `F5` / `F5 AA` 两字节），其余按文本处理
                int hold = PartialHeadTailLength();
                int emit = _pending.Count - hold;
                if (emit > 0)
                {
                    DrainAsText(_pending, 0, emit, events);
                    _pending.RemoveRange(0, emit);
                }
                break;
            }

            if (head > 0)
            {
                DrainAsText(_pending, 0, head, events);
                _pending.RemoveRange(0, head);
            }

            if (_pending.Count < CompanyFrame.HeaderLength)
                break;                                  // 还差 `LEN` 字节

            int len = _pending[3];
            if (len < 4)
            {
                // `LEN` 非法 → 丢当前 `F5`，从下一个字节重新找帧头（不把整段当文本，避免垃圾成行）
                _pending.RemoveAt(0);
                continue;
            }

            int total = CompanyFrame.HeaderLength + len;
            if (_pending.Count < total)
                break;                                  // 帧未收满，等后续字节

            var frame = _pending.GetRange(0, total).ToArray();
            _pending.RemoveRange(0, total);

            if (CompanyFrame.TryParse(frame, out _, out _, out _))
                events.Add(new StreamEvent(StreamEventKind.Frame, Frame: frame));
            // 结构非法（`BASE_ID ≠ 0x01` 等）→ 与 CompanyFrameReader 同口径：丢弃 + 重同步
        }

        if (_pending.Count > MaxPendingBytes)
        {
            DrainAsText(_pending, 0, _pending.Count, events);
            _pending.Clear();
        }

        return events;
    }

    /// <summary>会话结束：把未换行的半行文本与残留字节吐出来（不丢最后一行）</summary>
    public IReadOnlyList<StreamEvent> Flush()
    {
        var events = new List<StreamEvent>();
        EmitLine(events);

        if (_pending.Count > 0)
        {
            DrainAsText(_pending, 0, _pending.Count, events);
            _pending.Clear();
        }
        return events;
    }

    // ===== 内部 =====

    /// <summary>在 `_pending` 中找 `F5 AA AA` 的起始下标（不存在返回 -1）</summary>
    private int IndexOfHead()
    {
        for (int i = 0; i + 2 < _pending.Count; i++)
        {
            if (_pending[i] == CompanyFrame.Head0 &&
                _pending[i + 1] == CompanyFrame.Head1 &&
                _pending[i + 2] == CompanyFrame.Head2)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// 尾巴里"可能是被切断的帧头"的字节数（0 / 1 / 2）——只有它才需要留到下一段字节。
    /// 不留整两字节：否则纯文本流的最后两个字符会被无谓地延迟一行（参考实现不会这样）。
    /// </summary>
    private int PartialHeadTailLength()
    {
        int n = _pending.Count;
        if (n >= 2 && _pending[n - 2] == CompanyFrame.Head0 && _pending[n - 1] == CompanyFrame.Head1)
            return 2;
        if (n >= 1 && _pending[n - 1] == CompanyFrame.Head0)
            return 1;
        return 0;
    }

    /// <summary>把一段字节按文本处理：丢 `\r`、按 `\n` 切行、清 ANSI、UTF-8 解码</summary>
    private void DrainAsText(List<byte> source, int offset, int count, List<StreamEvent> events)
    {
        int end = offset + count;
        for (int i = offset; i < end; i++)
        {
            byte b = source[i];
            if (b == (byte)'\r')
                continue;
            if (b == (byte)'\n')
            {
                EmitLine(events);
                continue;
            }
            _lineBuffer.Add(b);
        }
    }

    /// <summary>当前行缓冲满一行即产出（空行不产出，与参考实现 `serial-chat` 一致）</summary>
    private void EmitLine(List<StreamEvent> events)
    {
        if (_lineBuffer.Count == 0)
            return;

        var text = Encoding.UTF8.GetString(_lineBuffer.ToArray());
        _lineBuffer.Clear();

        // 固件可能带 ANSI 颜色码（参考实现同样清理），清掉后再交给分域器
        text = AnsiRegex().Replace(text, string.Empty);
        if (text.Length > 0)
            events.Add(new StreamEvent(StreamEventKind.TextLine, Text: text));
    }

    [GeneratedRegex("\u001b\\[[0-9;]*m")]
    private static partial Regex AnsiRegex();
}
