// <copyright file="SerialLogWriter.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Text;
using PencilBox.Core.SerialScope;

namespace PencilBox.Infrastructure.Serial;

/// <summary>
/// 串口抓日志的**三份文件**写入器（主交错 / 每端口 / JSONL），按大小轮转。
///
/// 落盘格式（口径照抄已验证的参考实现 `serial-chat`，05 §2 的三份日志）：
/// - 主文件 `serial-scope.log`      ：`[yyyy-MM-dd HH:mm:ss.fff][LABEL][domain] 呈现`
/// - 每端口 `&lt;LABEL&gt;.log`          ：`[HH:mm:ss.fff][domain] 呈现`
/// - 机读 `serial-scope.jsonl`      ：`{"ts","port","source","domain","line"}`
///
/// ⚠️ `[domain]` 是我们相对参考实现**多加的一列**：它让"按域 grep"不必先懂设备文本格式
/// （05 §3.4「读日志（AI 用）」）；`decoded` 里的设备原件保持**原样不改**。
/// </summary>
public sealed class SerialLogWriter : IDisposable
{
    /// <summary>主（交错）日志文件名</summary>
    public const string MainFileName = "serial-scope.log";

    /// <summary>机读日志文件名</summary>
    public const string JsonlFileName = "serial-scope.jsonl";

    /// <summary>会话开始标记（`serial tail` 据此切"最近一次会话"，与参考实现一致）</summary>
    public const string SessionMarker = "===== SESSION START =====";

    private readonly LogRotationPolicy _rotation;
    private readonly Dictionary<string, FileSink> _perPort = new(StringComparer.Ordinal);
    private readonly FileSink _main;
    private readonly FileSink? _jsonl;

    public SerialLogWriter(string logDir, IEnumerable<string> labels, LogRotationPolicy rotation, bool enableJsonl)
    {
        LogDir = logDir;
        _rotation = rotation;

        Directory.CreateDirectory(logDir);
        _main = new FileSink(Path.Combine(logDir, MainFileName), rotation);
        _jsonl = enableJsonl ? new FileSink(Path.Combine(logDir, JsonlFileName), rotation) : null;

        foreach (var label in labels.Distinct(StringComparer.Ordinal))
            _perPort[label] = new FileSink(Path.Combine(logDir, $"{label}.log"), rotation);
    }

    /// <summary>日志目录（绝对路径，供 CLI 回显）</summary>
    public string LogDir { get; }

    /// <summary>主日志文件绝对路径（`serial tail` / `serial grep` 的读取目标）</summary>
    public string MainPath => Path.Combine(LogDir, MainFileName);

    /// <summary>某端口日志文件绝对路径</summary>
    public string PathFor(string label) => Path.Combine(LogDir, $"{label}.log");

    /// <summary>写会话开始标记（三份都写，便于任一份都能按会话切片）</summary>
    public void WriteSessionStart(DateTimeOffset timestamp)
    {
        var stamp = timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff");
        _main.WriteLine($"[{stamp}][SYS][{LogDomain.Sys}] {SessionMarker}");

        var shortStamp = timestamp.ToString("HH:mm:ss.fff");
        foreach (var sink in _perPort.Values)
            sink.WriteLine($"[{shortStamp}][{LogDomain.Sys}] {SessionMarker}");
    }

    /// <summary>写一行（线程模型：由单一"落盘泵"调用，无需加锁）</summary>
    public void Write(LogLine line)
    {
        var stamp = line.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff");
        _main.WriteLine($"[{stamp}][{line.PortName}][{line.Domain}] {line.Decoded}");

        if (_perPort.TryGetValue(line.PortName, out var sink))
            sink.WriteLine($"[{line.Timestamp:HH:mm:ss.fff}][{line.Domain}] {line.Decoded}");

        _jsonl?.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            ts = stamp,
            port = line.PortName,
            source = line.Source == LogSource.Frame ? "Frame" : "Text",
            domain = line.Domain,
            line = line.Decoded,
            raw = line.Raw
        }));
    }

    public void Dispose()
    {
        _main.Dispose();
        _jsonl?.Dispose();
        foreach (var sink in _perPort.Values)
            sink.Dispose();
        _perPort.Clear();
    }

    /// <summary>
    /// 清空该目录下**本工具产出**的日志（含轮转备份）。
    /// 默认启动即清空（05 §3.5）；`--no-clear` 走"追加"路径，不调用本方法。
    /// 文件被编辑器占用时退化为**截断**（编辑器里会看到内容清空后重新增长），不因占用而启动失败。
    /// </summary>
    public static void Clear(string logDir, IEnumerable<string> labels)
    {
        if (!Directory.Exists(logDir))
            return;

        var patterns = new List<string> { $"{MainFileName}*", $"{JsonlFileName}*" };
        patterns.AddRange(labels.Distinct(StringComparer.Ordinal).Select(l => $"{l}.log*"));

        foreach (var pattern in patterns)
        {
            foreach (var path in Directory.EnumerateFiles(logDir, pattern))
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    TryTruncate(path);
                }
                catch (UnauthorizedAccessException)
                {
                    TryTruncate(path);
                }
            }
        }
    }

    private static void TryTruncate(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite);
        }
        catch (Exception)
        {
            // 清不掉就保留原样：宁可留旧日志，也不要因为占用而启动失败
        }
    }

    /// <summary>按大小轮转的纯文本落盘单元</summary>
    private sealed class FileSink : IDisposable
    {
        private readonly string _path;
        private readonly LogRotationPolicy _policy;
        private FileStream _stream;
        private long _bytes;

        public FileSink(string path, LogRotationPolicy policy)
        {
            _path = path;
            _policy = policy;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _bytes = _stream.Length;
        }

        public void WriteLine(string text)
        {
            var buffer = Encoding.UTF8.GetBytes(text + "\n");

            // 写入前判断：超限则先轮转再写 ⇒ 当前文件始终含最新行（崩溃也不丢最后一行）
            if (_policy.ShouldRotate(_bytes, buffer.Length))
                Rotate();

            _stream.Write(buffer, 0, buffer.Length);
            _stream.Flush();
            _bytes += buffer.Length;
        }

        private void Rotate()
        {
            _stream.Dispose();

            // 最老的一份（下标 BackupCount-1）直接被覆盖丢弃：保留"当前 + BackupCount-1 份备份"
            for (int i = _policy.BackupCount - 2; i >= 1; i--)
            {
                var src = LogRotationPolicy.BackupName(_path, i);
                if (!File.Exists(src))
                    continue;
                File.Move(src, LogRotationPolicy.BackupName(_path, i + 1), overwrite: true);
            }

            File.Move(_path, LogRotationPolicy.BackupName(_path, 1), overwrite: true);
            _stream = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            _bytes = 0;
        }

        public void Dispose() => _stream.Dispose();
    }
}
