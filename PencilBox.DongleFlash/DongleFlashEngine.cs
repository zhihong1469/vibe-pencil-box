// <copyright file="DongleFlashEngine.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace PencilBox.DongleFlash;

/// <summary>烧录包 = `一键烧录脚本*` 文件夹（固件按 git 提交号命名，动态扫描不写死清单）。</summary>
public sealed record FlashPackage(string Directory, string FolderName, string DisplayName)
{
    public string FlashDir => Path.Combine(Directory, "flash");
    public string BatPath => Path.Combine(Directory, "一键烧录.bat");

    /// <summary>MD5SUMS.txt 在**包根**（与 bat 同级，01 号 §2.1 布局），清单内的文件名相对 `flash\` 解析。</summary>
    public string Md5SumsPath => Path.Combine(Directory, "MD5SUMS.txt");
}

/// <summary>
/// 动态扫描包根目录下的 `一键烧录脚本*`（01 号 P0-1）：入列门槛 = `flash\isd_download.exe` 与
/// `isd_config.ini` 在位；显示名优先解析 bat 横幅（如 `v1.0.1 (6a2fdc8)`），横幅缺失回退文件夹名。
/// </summary>
public static class PackageScanner
{
    public static List<FlashPackage> Scan(string rootDir)
    {
        var list = new List<FlashPackage>();
        if (!Directory.Exists(rootDir))
            return list;

        foreach (var dir in Directory.GetDirectories(rootDir, "一键烧录脚本*")
                     .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var flash = Path.Combine(dir, "flash");
            if (!File.Exists(Path.Combine(flash, "isd_download.exe")) ||
                !File.Exists(Path.Combine(flash, "isd_config.ini")))
                continue;   // 缺烧录工具/配置不入列表（半拷贝的包防误选）

            string? banner = null;
            var bat = Path.Combine(dir, "一键烧录.bat");
            if (File.Exists(bat))
                banner = BatBanner.Parse(File.ReadAllText(bat));   // bat 纯 ASCII（其头注自述），默认解码即可

            list.Add(new FlashPackage(dir, Path.GetFileName(dir), banner ?? Path.GetFileName(dir)));
        }

        return list;
    }
}

/// <summary>bat 横幅解析：`rem  Firmware : v1.0.1 (git 6a2fdc8), packaged ...` → `v1.0.1 (6a2fdc8)`。</summary>
public static class BatBanner
{
    private static readonly Regex Pattern =
        new(@"Firmware\s*:\s*(\S+)\s*\(git\s*([0-9a-fA-F]+)\)", RegexOptions.Compiled);

    public static string? Parse(string batText)
    {
        var m = Pattern.Match(batText);
        return m.Success ? $"{m.Groups[1].Value} ({m.Groups[2].Value})" : null;
    }
}

/// <summary>
/// PASS 判定（01 号 §2.3-2，本工具的灵魂规则）：**退出码 0 且输出含 "Write block"，缺一即 FAIL**。
/// 只看退出码会误判——板子不在下载态时工具打印 `Device offline, only package the file.` 照样 exit 0
/// （已发生过的假 PASS 事故，产线等于漏烧）。字样是 ASCII，GBK 解码不受影响。
/// </summary>
public static class PassRule
{
    public const string WriteEvidence = "Write block";

    public static bool IsPass(int exitCode, string output) =>
        exitCode == 0 && output.IndexOf(WriteEvidence, StringComparison.Ordinal) >= 0;
}

public sealed record Md5Report(bool Ok, IReadOnlyList<string> Problems);

/// <summary>
/// MD5 自检（01 号 P1-8）：选中包时对包根 `MD5SUMS.txt`（md5sum 格式 `hash *filename`，
// 兼容两空格文本模式）逐项比对 flash\ 内文件；列出的文件缺失或不符 = 不合格，阻止烧录。
/// 例外（10-23 三包取证）：jl_isd.bin / jl_isd.fw 是 isd_download 每次运行（含板不在线
/// "只打包文件"）都会重写的合并产物，首次烧录后清单哈希必失配（v1.0.2-rc.2 三连"自检不过"
/// 根因）——烧录命令不消费这两件，清单里列了也跳过比对。
/// </summary>
public static class Md5Checker
{
    private static readonly HashSet<string> VolatileArtifacts = new(StringComparer.OrdinalIgnoreCase)
        { "jl_isd.bin", "jl_isd.fw" };

    public static Md5Report Verify(string flashDir, string md5SumsText)
    {
        var problems = new List<string>();

        foreach (var raw in md5SumsText.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0)
                continue;

            string hash, name;
            var star = line.IndexOf('*');
            if (star > 0)
            {
                hash = line.Substring(0, star).Trim();
                name = line.Substring(star + 1).Trim();
            }
            else
            {
                var parts = line.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2)
                {
                    problems.Add($"MD5SUMS 行无法解析：{line}");
                    continue;
                }
                hash = parts[0];
                name = parts[1];
            }

            if (VolatileArtifacts.Contains(name))
                continue;   // 运行期产物（见类注）：烧录命令不消费，比对无意义

            if (hash.Length != 32 || !hash.All(Uri.IsHexDigit))
            {
                problems.Add($"MD5SUMS 哈希格式异常：{line}");
                continue;
            }

            var path = Path.Combine(flashDir, name.Replace('/', '\\'));
            if (!File.Exists(path))
            {
                problems.Add($"flash\\{name}：清单列出但文件缺失");
                continue;
            }

            using var md5 = System.Security.Cryptography.MD5.Create();
            using var fs = File.OpenRead(path);
            var actual = BitConverter.ToString(md5.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            if (actual != hash.ToLowerInvariant())
                problems.Add($"flash\\{name}：MD5 不符（清单 {hash.ToLowerInvariant()} / 实际 {actual}）");
        }

        if (problems.Count == 0)
            problems.Add("MD5 全部一致");   // Ok 时给 UI 一句正面摘要
        return new Md5Report(problems.Count == 1 && problems[0] == "MD5 全部一致", problems);
    }
}

public sealed record FlashRunResult(int ExitCode, string Output, TimeSpan Duration)
{
    public bool Pass => PassRule.IsPass(ExitCode, Output);
}

/// <summary>
/// 烧录编排（01 号 §2.2 命令行**一字不改**照抄）：在所选包 `flash\` 目录内调 isd_download，
/// 烧录动作全权交给工具本身。硬约束逐条落实：WorkingDirectory=flash（2.3-1，工具要在当前目录找
/// bd19loader.bin 等 12 个文件，禁拷临时目录）；GBK 解码（2.3-3）；参数不得修改（2.3-4，
/// `-format all` 量产全片擦 + `-reboot 2500` 烧完自动复位）；取消 = Kill 进程（等待板子
/// `-wait 300` 窗内可取消）。
/// </summary>
public sealed class FlashRunner
{
    public const string Arguments =
        "isd_config.ini -tonorflash -dev bd19 -boot 0x2000 -div8 -wait 300 " +
        "-key AC69.key -uboot uboot.boot -app app.bin cfg_tool.bin -res p11_code.bin " +
        "-uboot_compress -format all -reboot 2500";

    private static readonly Encoding Gbk = Encoding.GetEncoding(936);

    /// <param name="onLine">输出行回调（线程池线程上触发，UI 自行投递）。</param>
    public async Task<FlashRunResult> RunAsync(string flashDir, Action<string>? onLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(flashDir, "isd_download.exe"),
            Arguments = Arguments,
            WorkingDirectory = flashDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Gbk,
            StandardErrorEncoding = Gbk,
        };

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };
        process.Start();

        using var kill = ct.Register(() =>
        {
            try { process.Kill(); }
            catch { /* 已退出竞态，忽略 */ }
        });

        var stdout = PumpAsync(process.StandardOutput, onLine);
        var stderr = PumpAsync(process.StandardError, onLine);
        await Task.Run(() => process.WaitForExit(), ct).ConfigureAwait(false);
        var output = (await stdout.ConfigureAwait(false)) + "\r\n" + (await stderr.ConfigureAwait(false));

        int exitCode;
        try { exitCode = process.ExitCode; }
        catch { exitCode = -1; }   // Kill 竞态下拿不到退出码 → 非 0 必 FAIL
        return new FlashRunResult(exitCode, output, sw.Elapsed);
    }

    private static async Task<string> PumpAsync(StreamReader reader, Action<string>? onLine)
    {
        var sb = new StringBuilder();
        for (; ; )
        {
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line == null)
                break;
            sb.AppendLine(line);
            onLine?.Invoke(line);
        }
        return sb.ToString();
    }
}
