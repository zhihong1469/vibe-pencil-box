using System.Globalization;
using PencilBox.Core.SerialScope;

namespace PencilBox.Tests;

/// <summary>
/// S4 对拍（以**统一时间轴**为基准）：主日志行反解析 + 三侧对齐的黄金向量测试。
///
/// 样本口径：设备文本行取自**实测** `serial-chat/logs/ota-backup/02-ota-full_DONGLE.log`；
/// PC/设备帧用骨架 §6.1 权威 `LEN` 的黄金向量（`0x16/01` 总 20B、`0x16/07` 总 60B、`0x16/0A` 总 12B、`0x16/20` 总 11B）。
/// </summary>
public class SerialScopeDiffTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-09-15T19:33:15.000+08:00");

    private static byte[] Bytes(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    private static DateTimeOffset At(int ms) => T0.AddMilliseconds(ms);

    /// <summary>设备文本行（原文取自实测日志）</summary>
    private static LogLine DevText(int ms, string text)
        => new(At(ms), "DONGLE", LogSource.Text, new DeviceLogClassifier().Classify(text), text, text);

    /// <summary>
    /// 一次会话的组装器：**同一个端口共用一个管线**（与真实一致 —— 这样 `0x16/01` 记住的块总数
    /// 才能带到后续的 `数据块 packh=12/224`，用"每帧新建管线"会把分母丢掉）。
    /// </summary>
    private sealed class Session
    {
        private readonly SerialScopePipeline _port = new("PENCIL");
        private readonly List<LogLine> _lines = new();

        /// <summary>注入一帧黄金向量（下行/上行由帧内 BASECMD 决定）</summary>
        public Session Frame(int ms, string hex)
        {
            _lines.AddRange(_port.Feed(At(ms), Bytes(hex)));
            return this;
        }

        /// <summary>注入一行设备文本日志</summary>
        public Session Text(int ms, string text)
        {
            _lines.Add(DevText(ms, text));
            return this;
        }

        public List<LogLine> Build() => _lines;
    }

    // ===== 黄金帧 =====

    /// <summary>`0x16/01` 会话开始：总 20B，FILELEN=229298 ⇒ 224 块</summary>
    private const string SessionStart = "F5 AA AA 10 61 01 16 01 00 01 01 01 00 00 1C 4D 00 03 7F B2";

    /// <summary>`0x16/0A` 设备请求块（总 12B）</summary>
    private static string AskFrame(byte block) => $"F5 AA AA 08 E1 01 16 0A 00 00 {block:X2} 00";

    /// <summary>`0x16/07` PC 数据块（总 60B）</summary>
    private static string ChunkFrame(byte block) =>
        $"F5 AA AA 38 61 01 16 07 00 00 {block:X2} 07" + new string('0', 96);

    /// <summary>`0x16/20` 终态完成（总 11B）</summary>
    private const string ProgressDone = "F5 AA AA 07 E1 01 16 20 00 02 E0";

    /// <summary>3 块完整会话：设备请求（**同时有帧与文本两种形态**）→ PC 发送 → 设备报完成 → 复位</summary>
    private static Session PerfectSession()
    {
        var session = new Session().Frame(0, SessionStart);

        for (byte block = 0; block < 3; block++)
        {
            int baseMs = 10 + block * 30;
            session
                .Text(baseMs, $"[00:40:5{block}.939][Info]: [DONGLE][ota_usb] RUNNING: request packh={block}/224")
                .Frame(baseMs + 3, AskFrame(block))
                .Frame(baseMs + 4, ChunkFrame(block))
                .Text(baseMs + 10, $"[00:40:5{block}.281][Info]: [DONGLE][ota_usb] block {block} done ({(block + 1) * 1024}/229298 bytes)");
        }

        return session
            .Frame(200, ProgressDone)
            .Text(210, "[00:42:06.214][Info]: [DONGLE][ota_usb] DONE -> cpu_reset in 100ms")
            .Text(220, "[00:42:06.316][Info]: [DONGLE][ota_usb] cpu_reset() -> boot new bank");
    }

    // ===== 正常对拍 =====

    [Fact]
    public void Compare_PerfectSession_ReportsAligned()
    {
        var diff = OtaTimelineDiffer.Compare(PerfectSession().Build());

        Assert.Equal(3, diff.Blocks.Count);
        Assert.Equal(224, diff.BlocksExpected);
        Assert.Empty(diff.MissingPcSend);
        Assert.Empty(diff.MissingDeviceDone);
        Assert.Empty(diff.Duplicated);
        Assert.Empty(diff.OutOfOrder);
        Assert.StartsWith("✓", diff.Verdict);
        Assert.Equal(8, diff.FrameCount);      // 开始 1 + 每块（请求+数据）×3 + 终态 1
        Assert.Equal(8, diff.TextLineCount);   // 每块（请求+完成）×3 + 里程碑 2
    }

    [Fact]
    public void Compare_SameRequestSeenAsFrameAndText_IsNotDuplicated()
    {
        // 同一请求同时以 `↑0x0A` 帧与 `RUNNING: request packh=N` 文本出现是**常态**，不得判成重查
        var diff = OtaTimelineDiffer.Compare(PerfectSession().Build());

        Assert.Empty(diff.Duplicated);
        Assert.All(diff.Blocks, b => Assert.Equal(1, b.AskFrameCount));
        Assert.All(diff.Blocks, b => Assert.Equal(1, b.AskTextCount));
        Assert.All(diff.Blocks, b => Assert.Equal(2, b.AskCount));
    }

    [Fact]
    public void Compare_AskToSendLatencyUsesEarliestObservation()
    {
        var diff = OtaTimelineDiffer.Compare(PerfectSession().Build());

        // 设备请求最早出现在**文本**（+10ms），PC 在 +14ms 发出 ⇒ 4ms
        Assert.Equal(TimeSpan.FromMilliseconds(4), diff.AskToSendMedian);
        Assert.Equal(TimeSpan.FromMilliseconds(4), diff.AskToSendMax);
        Assert.Equal(TimeSpan.FromMilliseconds(6), diff.SendToDoneMedian);   // +14 → +20
    }

    [Fact]
    public void Compare_TerminalStateAndMilestonesAreCaptured()
    {
        var diff = OtaTimelineDiffer.Compare(PerfectSession().Build());

        Assert.Equal("[FRAME] ↑0x16 进度 packh=224/224 终态=完成 DOWNTYPE=00/基站", diff.PcTerminal);
        Assert.Contains("DONE -> cpu_reset", string.Join("|", diff.DeviceMilestones));
        Assert.Contains("boot new bank", string.Join("|", diff.DeviceMilestones));
    }

    [Fact]
    public void Compare_RoutineBlockDoneLines_AreNotMilestones()
    {
        // 回归：`DONE` 若大小写不敏感，这两条 `block N done` 会全被当成里程碑
        var diff = OtaTimelineDiffer.Compare(PerfectSession().Build());

        Assert.DoesNotContain(diff.DeviceMilestones, m => m.Contains(" done (", StringComparison.Ordinal));
    }

    // ===== 缺陷场景 =====

    [Fact]
    public void Compare_MissingPcSend_IsReportedPerBlock()
    {
        var lines = PerfectSession().Build();
        lines.RemoveAll(l => l.Source == LogSource.Frame &&
                             l.Decoded.Contains("数据块 packh=1/", StringComparison.Ordinal));

        var diff = OtaTimelineDiffer.Compare(lines);

        Assert.Equal(new[] { 1 }, diff.MissingPcSend);
        Assert.Equal(2, diff.Blocks.Count(b => b.SendCount > 0));
        Assert.Contains("PC 未发", diff.Verdict);
    }

    [Fact]
    public void Compare_MissingDeviceDone_IsReportedOnlyWithDeviceEvidence()
    {
        var lines = PerfectSession().Build();
        lines.RemoveAll(l => l.Decoded.Contains("block 2 done", StringComparison.Ordinal));

        var diff = OtaTimelineDiffer.Compare(lines);

        Assert.True(diff.HasDeviceDoneEvidence);
        Assert.Equal(new[] { 2 }, diff.MissingDeviceDone);
        Assert.Contains("设备未报完成", diff.Verdict);
    }

    [Fact]
    public void Compare_NoDeviceDoneEvidence_DoesNotClaimMissing()
    {
        // 设备侧从不打印 `block N done`（另一版固件）⇒ 不能把"设备未报完成"当缺失（不冤枉）
        var lines = PerfectSession().Build()
            .Where(l => !l.Decoded.Contains(" done (", StringComparison.Ordinal))
            .ToList();

        var diff = OtaTimelineDiffer.Compare(lines);

        Assert.False(diff.HasDeviceDoneEvidence);
        Assert.Empty(diff.MissingDeviceDone);
        Assert.Contains("不判定", diff.ToText());
    }

    [Fact]
    public void Compare_RepeatedAskFromSameSource_IsDuplicated()
    {
        var lines = PerfectSession().Build();
        // 同一来源（帧）对块 1 再请求一次
        lines.AddRange(new SerialScopePipeline("PENCIL").Feed(At(95), Bytes(AskFrame(1))));

        var diff = OtaTimelineDiffer.Compare(lines);

        Assert.Equal(new[] { 1 }, diff.Duplicated);
        Assert.Equal(2, diff.Blocks.Single(b => b.Block == 1).AskFrameCount);
        Assert.Contains("重查", diff.Verdict);
    }

    [Fact]
    public void Compare_OutOfOrderSend_IsReported()
    {
        // PC 在设备请求之前就把块 0 发了（时序违例）
        var lines = PerfectSession().Build()
            .Select(l => l.Source == LogSource.Frame && l.Decoded.Contains("数据块 packh=0/", StringComparison.Ordinal)
                ? l with { Timestamp = At(1) }
                : l)
            .ToList();

        var diff = OtaTimelineDiffer.Compare(lines);

        Assert.Single(diff.OutOfOrder);
        Assert.Contains("早于设备请求", diff.OutOfOrder[0]);
        Assert.Contains("时序违例", diff.Verdict);
    }

    // ===== 单侧/不可对拍 =====

    [Fact]
    public void Compare_OnlyFrames_TellsUserToAttachDeviceLog()
    {
        var lines = new Session().Frame(0, SessionStart).Frame(5, ChunkFrame(0)).Build();

        var diff = OtaTimelineDiffer.Compare(lines);

        Assert.Equal(0, diff.TextLineCount);
        Assert.Contains("没有设备文本日志", diff.Verdict);
    }

    [Fact]
    public void Compare_OnlyText_TellsUserToDecodeFrames()
    {
        var lines = new Session()
            .Text(0, "[00:40:52.939][Info]: [DONGLE][ota_usb] RUNNING: request packh=0/224")
            .Build();

        var diff = OtaTimelineDiffer.Compare(lines);

        Assert.Equal(0, diff.FrameCount);
        Assert.Contains("没有 F5 帧", diff.Verdict);
    }

    [Fact]
    public void Compare_RawFramesWithoutKeys_TellsUserToTurnDecodingOn()
    {
        // `--decode off` 抓的日志：帧在，但没有可对齐的块键
        var raw = new LogLine(At(0), "PENCIL", LogSource.Frame, LogDomain.F5,
            "F5AAAA386101160700000C07", "[FRAME] RAW F5AAAA386101160700000C07");

        var diff = OtaTimelineDiffer.Compare(new[] { raw });

        Assert.Empty(diff.Blocks);
        Assert.Contains("--decode off", diff.Verdict);
    }

    // ===== 主日志行反解析 =====

    [Fact]
    public void Parser_ReadsMainLogLine_EvenWhenDecodedContainsBrackets()
    {
        var raw = "[2026-09-15 19:33:15.447][DONGLE][ota_usb] [00:40:52.936][Info]: [DONGLE][TODO] ota_usb: dual_bank_update_allow_check(size=229298)";

        Assert.True(LogLineParser.TryParseMainLog(raw, out var line));
        Assert.Equal(DateTimeOffset.Parse("2026-09-15T19:33:15.447+08:00"), line.Timestamp);
        Assert.Equal("DONGLE", line.PortName);
        Assert.Equal(LogDomain.OtaUsb, line.Domain);
        Assert.Equal(LogSource.Text, line.Source);
        Assert.StartsWith("[00:40:52.936][Info]:", line.Decoded);
    }

    [Fact]
    public void Parser_MarksFrameLines()
    {
        var raw = "[2026-09-15 19:33:15.457][PENCIL][f5] [FRAME] ↓0x16 数据块 packh=12/224 packl=07";

        Assert.True(LogLineParser.TryParseMainLog(raw, out var line));
        Assert.Equal(LogSource.Frame, line.Source);
        Assert.Equal("PENCIL", line.PortName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage line")]
    [InlineData("[2026-09-15 19:33:15.447][DONGLE] only-two-groups")]
    [InlineData("[not-a-timestamp][DONGLE][ota_usb] text")]
    [InlineData("[2026-09-15 19:33:15.447][DONGLE][ota_usb]no-space-after-bracket")]
    public void Parser_RejectsForeignLines(string raw)
    {
        Assert.False(LogLineParser.TryParseMainLog(raw, out _));
    }

    [Fact]
    public void Parser_FeedThroughDiffer_WorksOnRealisticLogText()
    {
        // 端到端：主日志文本 → 反解析 → 对拍（CLI/UI 走的就是这条路）
        var session = PerfectSession().Build();

        var fileLines = session
            .Select(l => $"[{l.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}]" +
                         $"[{l.PortName}][{l.Domain}] {l.Decoded}")
            .ToList();

        var parsed = fileLines
            .Select(raw => LogLineParser.TryParseMainLog(raw, out var line) ? line : null)
            .Where(l => l != null)
            .Select(l => l!)
            .ToList();

        Assert.Equal(session.Count, parsed.Count);

        var diff = OtaTimelineDiffer.Compare(parsed);
        Assert.StartsWith("✓", diff.Verdict);
        Assert.Equal(3, diff.Blocks.Count);
    }
}
