using System.Globalization;

namespace PencilBox.Core.SerialScope;

/// <summary>
/// 本工具**主日志**（`serial-scope.log`）的一行 → <see cref="LogLine"/> 的反解析（纯逻辑）。
///
/// 主日志行格式（<see cref="Infrastructure 侧写入"/>）：`[yyyy-MM-dd HH:mm:ss.fff][LABEL][domain] 呈现`。
/// 该格式**自描述**（时间/端口/域都在行首固定位置），所以对拍可以直接读日志文件重建成统一时间轴，
/// 不必依赖进程内状态 —— CLI 与 UI 因此共用同一条对拍路径。
/// </summary>
public static class LogLineParser
{
    /// <summary>本工具主日志的时间戳格式</summary>
    public const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff";

    /// <summary>帧行的呈现前缀（与 <see cref="SerialScopePipeline.FramePrefix"/> 同源）</summary>
    private const string FramePrefix = SerialScopePipeline.FramePrefix;

    /// <summary>解析主日志的一行；不是本工具写的行（如被手工拼接的注释）返回 <c>false</c></summary>
    public static bool TryParseMainLog(string? rawLine, out LogLine line)
    {
        line = default!;

        if (string.IsNullOrEmpty(rawLine))
            return false;

        if (!TryTakeBracket(rawLine, 0, out var stamp, out int next))
            return false;
        if (!TryTakeBracket(rawLine, next, out var label, out next))
            return false;
        if (!TryTakeBracket(rawLine, next, out var domain, out next))
            return false;

        // 三组方括号之后必须有一个空格，再是呈现文本（呈现文本自身可含方括号）
        if (next >= rawLine.Length || rawLine[next] != ' ')
            return false;

        if (!DateTimeOffset.TryParseExact(stamp, TimestampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var timestamp))
            return false;

        var decoded = rawLine[(next + 1)..];
        var source = decoded.StartsWith(FramePrefix, StringComparison.Ordinal)
            ? LogSource.Frame
            : LogSource.Text;

        line = new LogLine(timestamp, label, source, domain, decoded, decoded);
        return true;
    }

    /// <summary>取 `[start]` 形式的一段（返回括号内内容与下一处下标）</summary>
    private static bool TryTakeBracket(string text, int start, out string value, out int next)
    {
        value = string.Empty;
        next = start;

        if (start >= text.Length || text[start] != '[')
            return false;

        int end = text.IndexOf(']', start + 1);
        if (end < 0)
            return false;

        value = text[(start + 1)..end];
        next = end + 1;
        return true;
    }
}
