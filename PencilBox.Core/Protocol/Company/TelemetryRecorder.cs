// <copyright file="TelemetryRecorder.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Text;

namespace PencilBox.Core.Protocol.Company;

/// <summary>
/// `0x62` 遥测流**录制器**（纯逻辑，无串口依赖——CLI `record` 与 Desktop 录波共用）：
/// 收帧打点（`pc_rx_ms` 用调用方时钟）、按 seq 回绕口径累计**丢流数**、算实测帧率、出 CSV。
/// CSV 列口径 = pc-issues 对齐卷 06 §2：`pc_rx_ts,seq,flags,raw_gx…out_y`（**原码直出不换算**，
/// 换算只发生在消费端——系数单点 <see cref="CompanyConfig"/>，避免双份系数漂移）。
/// </summary>
public sealed class TelemetryRecorder
{
    private readonly List<(long PcRxMs, TelemetryFrame Frame)> _samples = new();
    private ushort? _lastSeq;

    /// <summary>seq 缺口累计（u16 回绕；一次跳 N 计 N-1 丢）</summary>
    public int Dropped { get; private set; }

    /// <summary>已收帧数</summary>
    public int Count => _samples.Count;

    /// <summary>首帧 pc_rx_ms（无帧 null）</summary>
    public long? FirstRxMs { get; private set; }

    /// <summary>末帧 pc_rx_ms（无帧 null）</summary>
    public long? LastRxMs { get; private set; }

    /// <summary>录制样本（时标 + 原帧）</summary>
    public IReadOnlyList<(long PcRxMs, TelemetryFrame Frame)> Samples => _samples;

    /// <summary>
    /// 收一帧。seq 增量按 u16 回绕取模（`(cur-prev) & 0xFFFF`）；
    /// 增量 >1 且 ≤0x8000 记丢 `delta-1`（乱序/回绕方向的畸形增量不误计——笔端单发送者无乱序，
    /// 该分支只为容错）。
    /// </summary>
    public void Add(long pcRxMs, TelemetryFrame frame)
    {
        if (_lastSeq is ushort prev)
        {
            int delta = (frame.Seq - prev) & 0xFFFF;
            if (delta > 1 && delta <= 0x8000)
                Dropped += delta - 1;
        }

        _lastSeq = frame.Seq;
        FirstRxMs ??= pcRxMs;
        LastRxMs = pcRxMs;
        _samples.Add((pcRxMs, frame));
    }

    /// <summary>
    /// 实测帧率 = (N-1)/(末帧-首帧时标)；样本 &lt;2 或时间跨度 0 → 0（`stream on` 测窗同理）。
    /// </summary>
    public double Fps
    {
        get
        {
            if (_samples.Count < 2 || LastRxMs is not long last || FirstRxMs is not long first)
                return 0;
            var spanMs = last - first;
            return spanMs <= 0 ? 0 : (_samples.Count - 1) * 1000.0 / spanMs;
        }
    }

    /// <summary>丢流率（占应收=已收+丢）；无帧 → 0</summary>
    public double DroppedRatio => Count + Dropped == 0 ? 0 : (double)Dropped / (Count + Dropped);
}

/// <summary>`0x62` 录波 CSV 生成（单点：CLI `record` 与 Desktop 录波/回放导出共用同一格式）</summary>
public static class TelemetryCsv
{
    /// <summary>表头（pc-issues 对齐卷 06 §2 列序；pc_rx_ts=PC 毫秒时标，用于延迟估计）</summary>
    public const string Header =
        "pc_rx_ts,seq,flags,raw_gx,raw_gy,raw_gz,raw_ax,raw_ay,raw_az,rate_yaw,rate_roll,out_x,out_y";

    /// <summary>一行样本（原码直出：raw=LSB、rate=0.01dps 定点、out=HID 原值）</summary>
    public static string Row(long pcRxMs, TelemetryFrame f) =>
        $"{pcRxMs},{f.Seq},{f.Flags},{f.RawGx},{f.RawGy},{f.RawGz},{f.RawAx},{f.RawAy},{f.RawAz}," +
        $"{f.RateYaw},{f.RateRoll},{f.OutX},{f.OutY}";

    /// <summary>
    /// 元数据尾注（对齐卷 06 §2：fps/dropped/div/fw_ver）：`# meta: fps=..,dropped=..,div=..,fw=..`。
    /// fw 形如 `0.12.119`；读不到版本时传 null → `unknown`。
    /// </summary>
    public static string Meta(double fps, int dropped, byte divider, string? firmwareVersion) =>
        $"# meta: fps={fps:F1},dropped={dropped},div={divider},fw={(string.IsNullOrWhiteSpace(firmwareVersion) ? "unknown" : firmwareVersion.Trim())}";

    /// <summary>整文件文本：表头 + 样本行 + 尾注（UTF-8 无 BOM 由调用方负责）</summary>
    public static string Build(
        IEnumerable<(long PcRxMs, TelemetryFrame Frame)> samples,
        double fps, int dropped, byte divider, string? firmwareVersion)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Header);
        foreach (var (ts, f) in samples)
            sb.AppendLine(Row(ts, f));
        sb.Append(Meta(fps, dropped, divider, firmwareVersion));
        return sb.ToString();
    }
}
