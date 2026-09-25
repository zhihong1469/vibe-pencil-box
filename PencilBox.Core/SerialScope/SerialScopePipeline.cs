// <copyright file="SerialScopePipeline.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.SerialScope;

/// <summary>
/// **一条端口的字节流 → 统一时间轴上的 <see cref="LogLine"/>** 的组装管线（纯逻辑，零 IO）。
///
/// 三层串起来：<see cref="StreamDemuxer"/>（字节流 → 文本行 / F5 帧）
/// → <see cref="ILogClassifier"/>（文本行分域） / <see cref="IFrameDecoder"/>（帧解码）。
/// 05 §3.3「绑定点」的第 1、2 条都落在本类里。
/// </summary>
public sealed class SerialScopePipeline
{
    /// <summary>帧行的呈现前缀 —— 让 `grep "\[FRAME\]"` 能一次捞出所有协议帧（05 §3.3）</summary>
    public const string FramePrefix = "[FRAME] ";

    private readonly string _portName;
    private readonly bool _decodeFrames;
    private readonly ILogClassifier _classifier;
    private readonly IFrameDecoder _frameDecoder;
    private readonly StreamDemuxer _demuxer = new();

    /// <param name="portName">端口标签（写入每行的 <see cref="LogLine.PortName"/>）</param>
    /// <param name="decodeFrames">是否把 F5 帧解码成人类可读（关=原样 HEX，仍定帧、不搅乱文本）</param>
    public SerialScopePipeline(
        string portName,
        ILogClassifier? classifier = null,
        IFrameDecoder? frameDecoder = null,
        bool decodeFrames = true)
    {
        _portName = portName;
        _decodeFrames = decodeFrames;
        _classifier = classifier ?? new DeviceLogClassifier();
        _frameDecoder = frameDecoder ?? new CompanyFrameDecoder();
    }

    /// <summary>喂一段字节，产出本段新成形的日志行（同一主机时钟，故整段共用一个时间戳）</summary>
    public IReadOnlyList<LogLine> Feed(DateTimeOffset timestamp, ReadOnlySpan<byte> chunk)
        => ToLogLines(timestamp, _demuxer.Append(chunk));

    /// <summary>会话结束：吐出最后半行/残留字节（不丢行）</summary>
    public IReadOnlyList<LogLine> Flush(DateTimeOffset timestamp)
        => ToLogLines(timestamp, _demuxer.Flush());

    private List<LogLine> ToLogLines(DateTimeOffset timestamp, IReadOnlyList<StreamEvent> events)
    {
        var lines = new List<LogLine>(events.Count);

        foreach (var e in events)
        {
            if (e.Kind == StreamEventKind.TextLine)
            {
                var text = e.Text ?? string.Empty;
                lines.Add(new LogLine(timestamp, _portName, LogSource.Text,
                    _classifier.Classify(text), text, text));
                continue;
            }

            var frame = e.Frame ?? Array.Empty<byte>();
            var hex = Convert.ToHexString(frame);
            lines.Add(new LogLine(timestamp, _portName, LogSource.Frame, LogDomain.F5,
                hex, FramePrefix + Render(frame, hex)));
        }

        return lines;
    }

    /// <summary>帧呈现：解码成功用解码文本；关解码或识别不了 → **原样 HEX**（不冒充语义）</summary>
    private string Render(ReadOnlySpan<byte> frame, string hex)
    {
        if (!_decodeFrames)
            return "RAW " + hex;

        return _frameDecoder.Decode(frame) is { } decoded ? decoded : "RAW " + hex;
    }
}
