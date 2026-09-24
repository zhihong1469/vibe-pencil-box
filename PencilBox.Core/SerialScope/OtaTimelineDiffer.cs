using System.Text;
using System.Text.RegularExpressions;

namespace PencilBox.Core.SerialScope;

/// <summary>某个块在统一时间轴上的三个时刻（设备请求 → PC 发送 → 设备完成）</summary>
public sealed class BlockTimeline
{
    /// <summary>块号（`PACKH`）</summary>
    public int Block { get; init; }

    /// <summary>设备请求该块的最早时刻（`↑0x16 请求块` 与设备文本 `request packh=N` 取更早者）</summary>
    public DateTimeOffset? DeviceAskedAt { get; set; }

    /// <summary>PC 发出该块（`↓0x16 数据块`）</summary>
    public DateTimeOffset? PcSentAt { get; set; }

    /// <summary>设备报该块完成（设备文本 `block N done`）</summary>
    public DateTimeOffset? DeviceDoneAt { get; set; }

    /// <summary>设备**帧**形式的请求次数（`↑0x16 请求块`）</summary>
    public int AskFrameCount { get; set; }

    /// <summary>设备**文本**形式的请求次数（`request packh=N`）</summary>
    public int AskTextCount { get; set; }

    /// <summary>PC 发送次数（>1 = 重发）</summary>
    public int SendCount { get; set; }

    /// <summary>设备完成上报次数</summary>
    public int DoneCount { get; set; }

    /// <summary>设备请求次数（两个来源合计）</summary>
    public int AskCount => AskFrameCount + AskTextCount;

    /// <summary>
    /// 是否"重复请求"。⚠️ **按来源分别判**：同一个请求同时以帧和文本两种形态出现是**常态**
    /// （设备既回 `0x0A` 帧、又打印一行 `RUNNING: request packh=N`），那是一次事件的两处观察，
    /// 不是重查；只有**同一来源**对同一块请求了多次才算重查。
    /// </summary>
    public bool IsAskDuplicated => AskFrameCount > 1 || AskTextCount > 1;
}

/// <summary>
/// **对拍摘要**（05 §3.3 / §4 S4）：以**统一时间轴为基准**，把"PC 收发帧"与"设备日志"对齐后给出
/// 「谁先谁后 / 有无缺失 / 是否重查 / 终态」。
///
/// 为什么以时间轴为准：双口（笔端 + 基站 / 协议口 + 日志口）用的是**同一主机时钟、毫秒级**（05 §2），
/// 所以两块数据天然可排序 —— 不需要把两边的时钟对上，只需按时间把同一块号的三件事串起来。
/// </summary>
public sealed class OtaTimelineDiff
{
    /// <summary>预期块数（来自 `0x16 开始 … 块 N` 或设备日志 `packh=x/N`；未知为 0）</summary>
    public int BlocksExpected { get; init; }

    /// <summary>按块号升序的时间线</summary>
    public IReadOnlyList<BlockTimeline> Blocks { get; init; } = Array.Empty<BlockTimeline>();

    /// <summary>设备侧要过/做过、但 PC **没发**的块（漏发）</summary>
    public IReadOnlyList<int> MissingPcSend { get; init; } = Array.Empty<int>();

    /// <summary>PC 发过、设备**没报完成**的块（只在设备确实有 `block N done` 证据时才判定）</summary>
    public IReadOnlyList<int> MissingDeviceDone { get; init; } = Array.Empty<int>();

    /// <summary>同一侧出现 >1 次的块（重查/重发）</summary>
    public IReadOnlyList<int> Duplicated { get; init; } = Array.Empty<int>();

    /// <summary>时序违例（PC 早于设备请求 / 设备完成早于 PC 发送）</summary>
    public IReadOnlyList<string> OutOfOrder { get; init; } = Array.Empty<string>();

    /// <summary>PC 侧终态（`↑0x16 进度 … 终态=…`）；没有则 null</summary>
    public string? PcTerminal { get; init; }

    /// <summary>设备侧里程碑（`DONE` / `cpu_reset` / `golden selftest` / `[TODO]` / `[STUB]` / `FAIL`）</summary>
    public IReadOnlyList<string> DeviceMilestones { get; init; } = Array.Empty<string>();

    /// <summary>`设备请求 → PC 发送` 延迟中位/最大（谁先谁后）</summary>
    public TimeSpan? AskToSendMedian { get; init; }
    public TimeSpan? AskToSendMax { get; init; }

    /// <summary>`PC 发送 → 设备完成` 延迟中位/最大</summary>
    public TimeSpan? SendToDoneMedian { get; init; }
    public TimeSpan? SendToDoneMax { get; init; }

    /// <summary>本会话文本行数 / F5 帧数</summary>
    public int TextLineCount { get; init; }
    public int FrameCount { get; init; }

    /// <summary>设备侧到底有没有 `block N done` 证据（没有就不该把"设备未报完成"当缺失）</summary>
    public bool HasDeviceDoneEvidence { get; init; }

    /// <summary>一条话结论（CLI 首行 / UI 卡片标题用）</summary>
    public string Verdict
    {
        get
        {
            if (FrameCount == 0)
                return "本会话没有 F5 帧 ⇒ 无法对拍（抓日志时请开 --decode f5，或该口本来就不走 F5）";
            if (Blocks.Count == 0)
                return "有 F5 帧但取不到块键 ⇒ 无法对拍（多半是抓日志时用了 --decode off，帧只落了原始 HEX）";
            if (TextLineCount == 0)
                return "本会话没有设备文本日志 ⇒ 只能看 PC 侧（要双面证据请同时挂设备日志口）";
            if (MissingPcSend.Count > 0)
                return $"✗ 有 {MissingPcSend.Count} 块 PC 未发（设备侧有痕迹）";
            if (OutOfOrder.Count > 0)
                return $"✗ 有 {OutOfOrder.Count} 处时序违例";
            if (Duplicated.Count > 0)
                return $"⚠ 有 {Duplicated.Count} 块重查/重发，但无缺失";
            if (HasDeviceDoneEvidence && MissingDeviceDone.Count > 0)
                return $"✗ 有 {MissingDeviceDone.Count} 块设备未报完成";
            return "✓ 两侧对齐，无缺失 / 无重查 / 时序正常";
        }
    }

    /// <summary>渲染成文本报告（CLI 直接打印；UI 卡片复用同一份，避免两处口径漂移）</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[对拍摘要] {Verdict}");
        sb.AppendLine($"  基准：统一时间轴（本机时钟，毫秒级）· 本会话 {TextLineCount + FrameCount} 行" +
                      $"（设备文本 {TextLineCount} · F5 帧 {FrameCount}）");

        if (Blocks.Count > 0)
        {
            sb.AppendLine($"  块：预期 {(BlocksExpected > 0 ? BlocksExpected.ToString() : "未知")}" +
                          $" · 设备请求 {Blocks.Count(b => b.AskCount > 0)}" +
                          $" · PC 发送 {Blocks.Count(b => b.SendCount > 0)}" +
                          $" · 设备完成 {Blocks.Count(b => b.DoneCount > 0)}");
        }

        sb.AppendLine("  时序：" + Describe(AskToSendMedian, AskToSendMax, "设备请求→PC 发送") +
                      " ； " + Describe(SendToDoneMedian, SendToDoneMax, "PC 发送→设备完成"));

        sb.AppendLine("  缺失：" +
                      $"PC 未发 {FormatIds(MissingPcSend)}" +
                      $" ； 设备未报完成 {(HasDeviceDoneEvidence ? FormatIds(MissingDeviceDone) : "（设备侧无 block-done 证据，不判定）")}");

        sb.AppendLine($"  重查：{(Duplicated.Count == 0 ? "无" : FormatIds(Duplicated) + "（同一块被请求/发送多次）")}");
        sb.AppendLine($"  顺序违例：{(OutOfOrder.Count == 0 ? "无" : "有 " + OutOfOrder.Count + " 处（详见下）")}");

        foreach (var item in OutOfOrder.Take(10))
            sb.AppendLine($"    · {item}");

        sb.AppendLine($"  PC 侧终态：{PcTerminal ?? "（未见 `0x16/20` 进度/终态帧）"}");
        sb.AppendLine("  设备侧里程碑：" + (DeviceMilestones.Count == 0
            ? "（无）"
            : string.Join(" ； ", DeviceMilestones.Select(Trim))));

        return sb.ToString();
    }

    private static string Describe(TimeSpan? median, TimeSpan? max, string label)
        => median == null ? $"{label} 无配对样本" : $"{label} 中位 {median.Value.TotalMilliseconds:F0} ms / 最大 {max!.Value.TotalMilliseconds:F0} ms";

    private static string FormatIds(IReadOnlyList<int> ids)
    {
        if (ids.Count == 0)
            return "无";

        const int cap = 12;
        var head = string.Join(",", ids.Take(cap));
        return ids.Count > cap ? $"[{head} … 共 {ids.Count} 个]" : $"[{head}]";
    }

    private static string Trim(string text) => text.Length <= 120 ? text : text[..120] + "…";
}

/// <summary>
/// 把统一时间轴上的行**按块号对齐**成三时刻（设备请求 / PC 发送 / 设备完成）并出摘要（纯逻辑）。
///
/// 取键规则（两边都要认，缺一边就判不出"缺失"）：
/// | 来源 | 形态 | 取到什么 |
/// |---|---|---|
/// | PC 帧（下行） | `[FRAME] ↓0x16 数据块 packh=N …` | PC 发送块 N |
/// | PC 帧（下行） | `[FRAME] ↓0x16 开始 … 块 224 …` | 预期块总数 |
/// | 设备帧（上行） | `[FRAME] ↑0x16 请求块 packh=N` | 设备请求块 N |
/// | 设备帧（上行） | `[FRAME] ↑0x16 进度 packh=N 终态=…` | PC 侧终态 |
/// | 设备文本 | `… RUNNING: request packh=N/224` | 设备请求块 N（+总数） |
/// | 设备文本 | `… block N done (… bytes)` | 设备完成块 N |
/// </summary>
public static partial class OtaTimelineDiffer
{
    /// <summary>按时间线对齐并出摘要（输入顺序随意，内部按时间戳稳定排序）</summary>
    public static OtaTimelineDiff Compare(IEnumerable<LogLine> lines)
    {
        var blocks = new SortedDictionary<int, BlockTimeline>();
        var milestones = new List<string>();

        int expected = 0, textCount = 0, frameCount = 0, doneEvidence = 0;
        string? pcTerminal = null;

        foreach (var line in lines.OrderBy(l => l.Timestamp))
        {
            if (line.Source == LogSource.Frame)
            {
                frameCount++;
                MatchFrame(line, blocks, ref expected, ref pcTerminal);
            }
            else
            {
                textCount++;
                MatchDeviceText(line, blocks, ref expected, ref doneEvidence, milestones);
            }
        }

        var list = blocks.Values.ToList();

        // 缺失判定要有"另一侧的存在证据"，否则会把"该侧根本不打印这类行"误判成缺失
        var missingPcSend = list
            .Where(b => b.SendCount == 0 && (b.AskCount > 0 || b.DoneCount > 0))
            .Select(b => b.Block).ToList();

        var missingDone = doneEvidence > 0
            ? list.Where(b => b.SendCount > 0 && b.DoneCount == 0).Select(b => b.Block).ToList()
            : new List<int>();

        var duplicated = list
            .Where(b => b.IsAskDuplicated || b.SendCount > 1 || b.DoneCount > 1)
            .Select(b => b.Block).ToList();

        var outOfOrder = new List<string>();
        foreach (var b in list)
        {
            if (b.DeviceAskedAt is { } asked && b.PcSentAt is { } sent && sent < asked)
                outOfOrder.Add($"块 {b.Block}：PC 发送（{sent:HH:mm:ss.fff}）早于设备请求（{asked:HH:mm:ss.fff}）");
            if (b.PcSentAt is { } sent2 && b.DeviceDoneAt is { } done && done < sent2)
                outOfOrder.Add($"块 {b.Block}：设备完成（{done:HH:mm:ss.fff}）早于 PC 发送（{sent2:HH:mm:ss.fff}）");
        }

        var askToSend = Latencies(list, b => b.DeviceAskedAt, b => b.PcSentAt);
        var sendToDone = Latencies(list, b => b.PcSentAt, b => b.DeviceDoneAt);

        return new OtaTimelineDiff
        {
            BlocksExpected = expected,
            Blocks = list,
            MissingPcSend = missingPcSend,
            MissingDeviceDone = missingDone,
            Duplicated = duplicated,
            OutOfOrder = outOfOrder,
            PcTerminal = pcTerminal,
            DeviceMilestones = milestones,
            AskToSendMedian = Median(askToSend),
            AskToSendMax = askToSend.Count > 0 ? askToSend.Max() : null,
            SendToDoneMedian = Median(sendToDone),
            SendToDoneMax = sendToDone.Count > 0 ? sendToDone.Max() : null,
            TextLineCount = textCount,
            FrameCount = frameCount,
            HasDeviceDoneEvidence = doneEvidence > 0
        };
    }

    // ===== 取键 =====

    private static void MatchFrame(LogLine line, SortedDictionary<int, BlockTimeline> blocks,
        ref int expected, ref string? pcTerminal)
    {
        var text = line.Decoded;

        if (PcChunkRegex().Match(text) is { Success: true } chunk)
        {
            var b = Get(blocks, int.Parse(chunk.Groups["n"].Value));
            b.PcSentAt ??= line.Timestamp;
            b.SendCount++;
            return;
        }

        if (SessionStartRegex().Match(text) is { Success: true } start)
        {
            expected = int.Parse(start.Groups["total"].Value);
            return;
        }

        if (DeviceAskRegex().Match(text) is { Success: true } ask)
        {
            var b = Get(blocks, int.Parse(ask.Groups["n"].Value));
            b.DeviceAskedAt = Earliest(b.DeviceAskedAt, line.Timestamp);
            b.AskFrameCount++;
            return;
        }

        if (text.Contains("0x16 进度", StringComparison.Ordinal))
            pcTerminal = text;   // 终态取最后一条（会话结束语在最后）
    }

    private static void MatchDeviceText(LogLine line, SortedDictionary<int, BlockTimeline> blocks,
        ref int expected, ref int doneEvidence, List<string> milestones)
    {
        var text = line.Decoded;

        if (TextAskRegex().Match(text) is { Success: true } ask)
        {
            var b = Get(blocks, int.Parse(ask.Groups["n"].Value));
            b.DeviceAskedAt = Earliest(b.DeviceAskedAt, line.Timestamp);
            b.AskTextCount++;

            if (int.TryParse(ask.Groups["total"].Value, out int total) && total > 0)
                expected = total;
        }

        if (TextDoneRegex().Match(text) is { Success: true } done)
        {
            var b = Get(blocks, int.Parse(done.Groups["n"].Value));
            b.DeviceDoneAt = Earliest(b.DeviceDoneAt, line.Timestamp);
            b.DoneCount++;
            doneEvidence++;
        }

        if (IsMilestone(text) && milestones.Count < 24)
            milestones.Add(text);
    }

    /// <summary>
    /// 设备侧"值得一眼看到"的里程碑。
    /// ⚠️ `DONE` **必须大小写敏感** —— 否则每条例行 `block N done` 都会被当成里程碑（224 条噪音）。
    /// </summary>
    private static bool IsMilestone(string text)
        => TerminalMilestoneRegex().IsMatch(text) || PlaceholderRegex().IsMatch(text);

    private static BlockTimeline Get(SortedDictionary<int, BlockTimeline> blocks, int block)
    {
        if (!blocks.TryGetValue(block, out var item))
        {
            item = new BlockTimeline { Block = block };
            blocks[block] = item;
        }
        return item;
    }

    /// <summary>取更早的时刻（两个来源观察同一事件时，以先发生的为准）</summary>
    private static DateTimeOffset Earliest(DateTimeOffset? current, DateTimeOffset candidate)
        => current is null || candidate < current.Value ? candidate : current.Value;

    // ===== 统计 =====

    private static List<TimeSpan> Latencies(IEnumerable<BlockTimeline> blocks,
        Func<BlockTimeline, DateTimeOffset?> from, Func<BlockTimeline, DateTimeOffset?> to)
        => blocks
            .Where(b => from(b) is not null && to(b) is not null)
            .Select(b => to(b)!.Value - from(b)!.Value)
            .ToList();

    private static TimeSpan? Median(List<TimeSpan> values)
    {
        if (values.Count == 0)
            return null;

        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[mid]
            : TimeSpan.FromTicks((sorted[mid - 1].Ticks + sorted[mid].Ticks) / 2);
    }

    // ===== 取键正则（口径见类注释的表） =====

    /// <summary>`[FRAME] ↓0x16 数据块 packh=12/224 packl=07`</summary>
    [GeneratedRegex(@"↓0x16 数据块 packh=(?<n>\d+)")]
    private static partial Regex PcChunkRegex();

    /// <summary>`[FRAME] ↓0x16 开始 … 块 224 …`</summary>
    [GeneratedRegex(@"0x16 开始 .*块 (?<total>\d+)")]
    private static partial Regex SessionStartRegex();

    /// <summary>`[FRAME] ↑0x16 请求块 packh=12/224`</summary>
    [GeneratedRegex(@"↑0x16 请求块 packh=(?<n>\d+)")]
    private static partial Regex DeviceAskRegex();

    /// <summary>设备文本 `RUNNING: request packh=0/224`</summary>
    [GeneratedRegex(@"request packh=(?<n>\d+)(?:/(?<total>\d+))?")]
    private static partial Regex TextAskRegex();

    /// <summary>设备文本 `block 0 done (1024/229298 bytes)`</summary>
    [GeneratedRegex(@"block (?<n>\d+) done")]
    private static partial Regex TextDoneRegex();

    /// <summary>设备侧终态类里程碑（**大小写敏感**：`DONE ->` 才认，不认 `block N done`）</summary>
    [GeneratedRegex(@"DONE ->|cpu_reset|boot new bank|golden selftest|\bFAIL")]
    private static partial Regex TerminalMilestoneRegex();

    /// <summary>占位未接标记（大小写不敏感，坑点 §八"占位必须显式标注"）</summary>
    [GeneratedRegex(@"\[TODO\]|\[STUB\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();
}
