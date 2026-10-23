// <copyright file="FlashLog.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.IO;
using System.Text;

namespace PencilBox.DongleFlash;

/// <summary>
/// 烧录日志落盘（10-23 指挥官反馈：MD5 拦截/FAIL 证据只滚在窗里，产线排障没凭据）。
/// 按天追加写 exe 同级 `log\DongleFlash_yyyyMMdd.log`，每行带时分秒前缀；目录打不开时
/// 降级为不落盘（返 null），绝不影响烧录主流程。只允许 UI 线程写（MainForm.AppendLog 单点镜像）。
/// </summary>
public sealed class FlashLog : IDisposable
{
    private readonly StreamWriter _writer;

    private FlashLog(StreamWriter writer, string filePath)
    {
        _writer = writer;
        FilePath = filePath;
    }

    public string FilePath { get; }

    public static FlashLog? OpenDaily(string baseDir)
    {
        try
        {
            var dir = Path.Combine(baseDir, "log");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "DongleFlash_" + DateTime.Now.ToString("yyyyMMdd") + ".log");
            var writer = new StreamWriter(path, append: true, Encoding.UTF8) { AutoFlush = true };
            return new FlashLog(writer, path);
        }
        catch (Exception)
        {
            return null;   // log 目录不可写/被占用：不落盘，烧录照常
        }
    }

    public void WriteLine(string line) =>
        _writer.WriteLine(DateTime.Now.ToString("HH:mm:ss ") + line);

    public void Dispose() => _writer.Dispose();
}
