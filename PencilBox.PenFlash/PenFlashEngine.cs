// <copyright file="PenFlashEngine.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Diagnostics;
using System.Management;
using System.Text;

namespace PencilBox.PenFlash;

/// <summary>
/// 烧录引擎：材料定位、固件扫描、CH340 探测、OmBleIsp 进程编排。
/// 烧录动作全权交给 OmBleIsp.exe（01 号「套壳不重写」）；调用参数/工作目录/编码
/// 逐条复刻 flash_r1p.ps1，改动须与 ps1 双向同步。
/// </summary>
public sealed class PenFlashEngine
{
    /// <summary>材料根 = 含 OmBleISP_cmd_tool 的目录（生产使用\R1P）。</summary>
    public string MaterialsRoot { get; }
    public string ToolDir { get; }
    public string ToolExe { get; }
    public string FirmwareDir { get; }

    public PenFlashEngine(string? materialsRoot = null)
    {
        MaterialsRoot = materialsRoot ?? ResolveMaterialsRoot()
            ?? throw new DirectoryNotFoundException(
                "找不到 R1P 材料（OmBleISP_cmd_tool）。可将材料目录放到 exe 旁，或在 exe 旁建「材料路径.txt」写一行材料根路径。");
        ToolDir = Path.Combine(MaterialsRoot, "OmBleISP_cmd_tool");
        ToolExe = Path.Combine(ToolDir, "OmBleIsp.exe");
        FirmwareDir = Path.Combine(MaterialsRoot, "固件");
        if (!File.Exists(ToolExe))
            throw new FileNotFoundException("找不到烧录工具 OmBleIsp.exe", ToolExe);
    }

    /// <summary>
    /// 材料根定位（依序探测，取第一个命中）：
    /// exe 旁「材料路径.txt」首行 → exe 目录本身 → exe 目录下「生产使用\R1P」→
    /// 开发机便利路径（output\produce\R1P → output\custom\生产使用\R1P）。
    /// </summary>
    public static string? ResolveMaterialsRoot()
    {
        var exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        try
        {
            var cfg = Path.Combine(exeDir, "材料路径.txt");
            if (File.Exists(cfg))
            {
                var line = File.ReadAllLines(cfg, Encoding.UTF8)
                    .Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal));
                if (line is not null && HasTool(line)) return line;
            }
        }
        catch { /* 配置读失败走自动探测 */ }

        foreach (var probe in new[]
                 {
                     exeDir,
                     Path.Combine(exeDir, "生产使用", "R1P"),
                     Path.GetFullPath(Path.Combine(exeDir, "..", "..", "custom", "生产使用", "R1P")),
                 })
        {
            if (HasTool(probe)) return probe;
        }
        return null;

        static bool HasTool(string dir) => File.Exists(Path.Combine(dir, "OmBleISP_cmd_tool", "OmBleIsp.exe"));
    }

    // ---- 固件扫描（动态，不写死清单；01 号 P0-1）----

    public List<FirmwareEntry> ScanFirmwares()
    {
        var dir = new DirectoryInfo(FirmwareDir);
        if (!dir.Exists) return new List<FirmwareEntry>();
        return dir.GetFiles("*.bin")
            .OrderByDescending(f => f.LastWriteTime)
            .Select(f => new FirmwareEntry(f.FullName, f.Name, f.Length, new DateTimeOffset(f.LastWriteTime)))
            .ToList();
    }

    /// <summary>出货版本口径：固件\一期出货使用的*.txt 首个命中的有效内容（Pencil 端补内容后生效）。</summary>
    public string? ReadShippingVersion()
    {
        try
        {
            var dir = new DirectoryInfo(FirmwareDir);
            if (!dir.Exists) return null;
            var txt = dir.GetFiles("一期出货使用的*.txt").OrderBy(f => f.Name).FirstOrDefault();
            if (txt is null) return null;
            return PenFlashCore.NormalizeShippingVersion(File.ReadAllText(txt.FullName, Encoding.UTF8));
        }
        catch { return null; }
    }

    // ---- CH340 探测（VID:PID 定位，不写死 COM 号；多候选不猜——UI 强制手选）----

    public List<string> DetectCh340Ports()
    {
        var ports = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT Name FROM Win32_PnPEntity WHERE DeviceID LIKE '%{PenFlashCore.Ch340VidPid}%'");
            foreach (var o in searcher.Get())
            {
                var com = PenFlashCore.ExtractComName(o["Name"]?.ToString());
                if (com is not null && !ports.Contains(com))
                    ports.Add(com);
            }
        }
        catch (Exception ex)
        {
            Warning?.Invoke(this, $"串口枚举失败：{ex.Message}（可手动输入口名）");
        }
        return ports;
    }

    public event EventHandler<string>? Warning;

    // ---- OmBleIsp 调用（ps1 Invoke-OmBleIsp 复刻：工具目录内跑 + cmd 混流 + GBK）----

    /// <summary>
    /// 在 OmBleISP_cmd_tool 目录内执行 OmBleIsp.exe（必须在自己目录内按裸文件名运行，01 号 §2.2）。
    /// 输出按 GBK(936) 解码（中文部分为 GBK，关键行全 ASCII）；stderr 经 cmd `2>&1` 并入 stdout
    /// （ps1 实测：PS 侧直接合并会在 ErrorRecord 上打断烧录，故沿用 cmd 层合并）。
    /// 返回 (退出码, 全量输出)。
    /// </summary>
    /// <summary>
    /// 跑 OmBleIsp。<paramref name="onLine"/> 逐行实时回调（烧录进度刷新用，工具线程调用）。
    /// 10-23 起改逐行读：此前 ReadToEnd 收整段，日志时间戳全挤在进程退出时刻（失真），进度也不可见。
    /// </summary>
    public async Task<(int ExitCode, string Output)> RunOmBleIspAsync(string arguments, CancellationToken ct, Action<string>? onLine = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c cd /d \"{ToolDir}\" && OmBleIsp.exe {arguments} 2>&1",
            WorkingDirectory = ToolDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,   // cmd 已 2>&1 合并；仍开重定向防句柄继承阻塞
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.GetEncoding(936),
            StandardErrorEncoding = Encoding.GetEncoding(936),
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("OmBleIsp.exe 启动失败");
        var readTask = Task.Run(async () =>
        {
            var lines = new List<string>();
            try
            {
                while (await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    lines.Add(line);
                    onLine?.Invoke(line);
                }
            }
            catch { /* 进程被杀时读流可能抛——尽力保留已读行 */ }
            return lines;
        });
        var stderrTask = proc.StandardError.ReadToEndAsync();   // 排空防缓冲满死锁（内容经 cmd 已并入 stdout）
        try
        {
            // net48 无 WaitForExitAsync：线程池等待 + 取消时杀进程树
            await Task.Run(() => proc.WaitForExit(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            try { proc.WaitForExit(3000); } catch { /* 尽力收尸 */ }
            throw;
        }
        var lines2 = await readTask.ConfigureAwait(false);
        try { await stderrTask.ConfigureAwait(false); } catch { /* 已排空即可 */ }
        return (proc.ExitCode, string.Join("\n", lines2));
    }

    private static void TryKill(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(); }
        catch { /* 进程已退/无权限——尽力争收 */ }
    }
}
