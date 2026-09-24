using System.Globalization;
using System.Text.RegularExpressions;

namespace PencilBox.Core.SerialScope;

/// <summary>一行 [AUSTAT] 遥测的解析结果（基站 D3 计数器链 1s 汇总，Dongle/04 §5.2）</summary>
public sealed record AustatSample(
    DateTimeOffset Timestamp,
    string Port,
    int WatermarkBytes,
    int WatermarkMinBytes,
    int WatermarkMaxBytes,
    int CbufFull,
    int AllocFail,
    int UsbFull,
    int SeqJump,
    int ConcealFrames,
    int ConcealEvents,
    int ConcealSkip);

/// <summary>
/// 解析基站串口日志里的 <c>[AUSTAT] wl=..(min-max)B cbuf_full=n alloc=n usb_full=n jump=n conceal(f/e/skip)=a/b/c</c> 行。
/// 字段容错：缺哪项填 0（固件演进加减字段不该把解析打崩）。
/// </summary>
public static partial class AustatParser
{
    [GeneratedRegex(
        @"wl=(?<wl>\d+)\((?<wmin>\d+)-(?<wmax>\d+)\)B"
        + @"(?:\s+cbuf_full=(?<cbuf>\d+))?"
        + @"(?:\s+alloc=(?<alloc>\d+))?"
        + @"(?:\s+usb_full=(?<usb>\d+))?"
        + @"(?:\s+jump=(?<jump>\d+))?"
        + @"(?:\s+conceal\(f/e/skip\)=(?<cf>\d+)/(?<ce>\d+)/(?<cs>\d+))?",
        RegexOptions.CultureInvariant)]
    private static partial Regex AustatRegex();

    /// <summary>从主日志一行解析；不是 [AUSTAT] 行返回 false</summary>
    public static bool TryParse(LogLine line, out AustatSample sample)
        => TryParseText(line.Timestamp, line.PortName, line.Raw, out sample);

    public static bool TryParseText(DateTimeOffset timestamp, string port, string text, out AustatSample sample)
    {
        sample = new AustatSample(timestamp, port, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var idx = text.IndexOf("[AUSTAT]", StringComparison.Ordinal);
        if (idx < 0) return false;

        var m = AustatRegex().Match(text[idx..]);
        if (!m.Success) return false;

        int G(string name) => m.Groups[name].Success ? int.Parse(m.Groups[name].Value, CultureInfo.InvariantCulture) : 0;
        sample = new AustatSample(
            timestamp,
            port,
            G("wl"), G("wmin"), G("wmax"),
            G("cbuf"), G("alloc"), G("usb"), G("jump"),
            G("cf"), G("ce"), G("cs"));
        return true;
    }
}
