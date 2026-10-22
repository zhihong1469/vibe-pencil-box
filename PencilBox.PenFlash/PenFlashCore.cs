// <copyright file="PenFlashCore.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.PenFlash;

/// <summary>
/// 纯逻辑核心（全部静态无副作用，进单测）：PASS 判定、COM→OmBleIsp 口换算、
/// 固件名解析、出货版默认选择策略、每口唯一拷贝名。规则逐条复刻自已验证的
/// flash_r1p.ps1（量产保障/isp烧录/Pencil/01 号 §2.2 = 需求本体），改动须双向同步。
/// 注意：本文件同时被 net48 编译——字符串 API 须用 net48 兼容形态
/// （Contains 无 StringComparison 重载 → IndexOf；StartsWith 无 char 重载）。
/// </summary>
public static class PenFlashCore
{
    /// <summary>CH340 治具的 USB 身份（口名随插口变动，只认 VID:PID）。</summary>
    public const string Ch340VidPid = "VID_1A86&PID_7523";

    public const int IspBaudrate = 115200;
    public const int MonitorBaudrate = 1000000;   // 笔端 UART0 日志口 = 1M（ps1 $MonitorBaud）

    // ARM/ESP 档实测映射与时序（两档一致，ps1 头注）：RST←RTS 反相、BOOT←DTR 反相
    public const int TSettleMs = 100;
    public const int TResetMs = 100;
    public const int TBootHoldMs = 500;

    // ---- PASS 判定（01 号 §2.2：退出码 0 且输出含 Finished Code:0，二者缺一不可）----

    /// <summary>OmBleIsp 逐包进度行（每 ~1% 一行），日志过滤用。</summary>
    public const string ProgrammingLineMark = "\"Status\":\"Programming\"";

    /// <summary>厂商终态行字面（01 号口径）。</summary>
    public const string FinishedMark = "\"Status\":\"Finished\",\"Code\":0";

    private const string FinishedKey = "\"Status\":\"Finished\"";
    private const string CodeZeroKey = "\"Code\":0";

    /// <summary>
    /// PASS ⇔ 退出码 == 0 且输出含终态证据。终态匹配用字面 + 宽松双条件兜底
    /// （厂商输出若在两个键之间插入空白，字面匹配会漏判——双条件等价语义）。
    /// </summary>
    public static bool IsPass(int exitCode, string output)
    {
        if (exitCode != 0) return false;
        if (string.IsNullOrEmpty(output)) return false;
        if (output.IndexOf(FinishedMark, StringComparison.Ordinal) >= 0) return true;
        return output.IndexOf(FinishedKey, StringComparison.Ordinal) >= 0
            && output.IndexOf(CodeZeroKey, StringComparison.Ordinal) >= 0;
    }

    /// <summary>日志行是否为刷屏进度行（过滤，只留 Finished/Fail/Error）。</summary>
    public static bool IsProgressNoise(string? line) =>
        line is not null && line.IndexOf(ProgrammingLineMark, StringComparison.Ordinal) >= 0;

    // ---- 口换算（ps1 Convert-OmPort：COM20 → /dev/ttyS19）----

    public static string ConvertOmPort(string comPort)
    {
        if (string.IsNullOrWhiteSpace(comPort)) return comPort ?? "";
        var m = ComNameRegex.Match(comPort.Trim());
        return m.Success ? "/dev/ttyS" + (int.Parse(m.Groups[1].Value) - 1) : comPort.Trim();
    }

    /// <summary>从 PnP 设备名（如 "USB-SERIAL CH340 (COM20)"）提取口名；无口名返回 null。</summary>
    public static string? ExtractComName(string? pnpName)
    {
        if (string.IsNullOrEmpty(pnpName)) return null;
        var m = ComNameRegex.Match(pnpName);
        return m.Success ? "COM" + m.Groups[1].Value : null;
    }

    /// <summary>多候选选口策略：唯一候选才自动选中；多候选禁止猜口（01 号 §2.3-5）。</summary>
    public static string? PickDefaultPort(IReadOnlyList<string> ports) =>
        ports.Count == 1 ? ports[0] : null;

    // ---- 固件名解析 ----

    /// <summary>pen_r1_v10_v0.13.21_target_20261014_gcc.bin → 显示 "v0.13.21 (20261014)"。</summary>
    public static string DescribeFirmware(string fileName)
    {
        var m = FirmwareNameRegex.Match(fileName);
        if (m.Success)
            return $"{m.Groups["ver"].Value} ({m.Groups["date"].Value})";
        return Path.GetFileNameWithoutExtension(fileName);   // 非常规命名回退，不阻塞
    }

    /// <summary>从文件名解析版本 token（如 "v0.13.21"），供出货版比对；解析不出返回 null。</summary>
    public static string? ParseFirmwareVersion(string fileName) =>
        FirmwareNameRegex.Match(fileName) is { Success: true } m ? m.Groups["ver"].Value : null;

    /// <summary>每口唯一拷贝名（并发防同名写冲突，02 号裁决 4-②）：COM20 → fw_COM20.bin。</summary>
    public static string UniqueFirmwareCopyName(string comPort) =>
        $"fw_{SanitizePort(comPort)}.bin";

    private static string SanitizePort(string comPort)
    {
        var sb = new System.Text.StringBuilder(comPort.Length);
        foreach (var c in comPort)
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }

    // ---- 出货版默认选择（策略：唯一匹配才默认；0 或多个 = 不猜，操作员手选）----

    public static string? SelectShippingFirmware(IReadOnlyList<FirmwareEntry> firmwares, string? shippingVersion)
    {
        if (string.IsNullOrWhiteSpace(shippingVersion)) return null;
        var wanted = shippingVersion.Trim();
        var matches = firmwares
            .Where(f =>
            {
                var ver = ParseFirmwareVersion(f.FileName);
                return (ver is not null && string.Equals(ver, wanted, StringComparison.OrdinalIgnoreCase))
                    || f.FileName.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0;
            })
            .ToList();
        return matches.Count == 1 ? matches[0].Path : null;
    }

    public static string? NormalizeShippingVersion(string? txtContent)
    {
        if (string.IsNullOrWhiteSpace(txtContent)) return null;
        var line = txtContent.Split(new[] { '\n', '\r' }).Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal));
        if (line is null) return null;
        // 行内可能是整句，抓版本号 token（v1.2.3 / v1.2.3-rc.1）
        var m = VersionTokenRegex.Match(line);
        return m.Success ? m.Value : null;
    }

    // ---- 错误码中文对照（照抄 ps1 $ErrorCodes，来源开发侧 error_codes.json）----

    public static string DescribeErrorCode(int rc) => rc switch
    {
        0 => "成功",
        2 => "文件不存在 → 检查固件路径/文件名",
        6 => "串口设备不存在 → 检查口号/是否插好",
        13 => "串口被占用 → 关掉串口助手/PencilBox/OmBleIspStudio 再试",
        -1001 => "通信链路未建立 → 芯片没进 ISP：查档位/接线/串口占用；先重插 USB 或退出 ISP 再试",
        -1007 => "发送失败 → 查接线；确认芯片在 ISP 态；可降波特率重试",
        -1201 => "命令行参数非法",
        -1203 => "芯片无响应 → 确认已进 ISP、查接线，或重新复位再试",
        -1206 => "Flash 读写失败 → 可先擦除再烧",
        -1207 => "固件文件打不开 → 检查文件是否存在",
        -1302 => "固件与芯片型号不匹配 → 确认 bin 是 R1P 的",
        -1303 => "Flash 数据 CRC 校验失败 → 重烧一次，仍失败换线/换口",
        -1401 => "不支持的芯片型号",
        -1402 => "不支持的 FLASH → 片选用 0（内部 Flash）",
        _ => $"未知返回码 {rc}"
    };

    private static readonly System.Text.RegularExpressions.Regex ComNameRegex =
        new(@"COM\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // pen_r1_v10_v0.13.21_target_20261014_gcc.bin → ver=v0.13.21, date=20261014
    private static readonly System.Text.RegularExpressions.Regex FirmwareNameRegex =
        new(@"^pen_r1_v10_(?<ver>v[0-9][\w.\-]*?)_target_(?<date>\d{8})_", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex VersionTokenRegex =
        new(@"v[0-9]+\.[0-9]+\.[0-9]+(?:[-.][0-9A-Za-z]+)*", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}

/// <summary>固件条目（扫描 固件\*.bin 的结果）。</summary>
public sealed record FirmwareEntry(string Path, string FileName, long Length, DateTimeOffset Modified);
