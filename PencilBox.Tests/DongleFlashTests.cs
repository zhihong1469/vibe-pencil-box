// <copyright file="DongleFlashTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.DongleFlash;

namespace PencilBox.Tests;

/// <summary>
/// R1D 产线烧录 UI 引擎单测（量产保障/isp烧录/Dongle/01+02 号）。
/// 核心是 01 号 §2.3-2 的 PASS 证据规则——拔板假 PASS（Device offline + exit 0）必须判 FAIL。
/// </summary>
public class DongleFlashTests
{
    // ================= PASS 证据规则（灵魂，01 号验收 2 的单元级兜底） =================

    [Fact]
    public void PassRule_WriteEvidenceWithZeroExit_Passes()
    {
        const string ok = """
            Erasing flash...
            Write block 0x00000000 - 0x0000FFFF
            Write block 0x00010000 - 0x0001FFFF
            Reboot done.
            """;
        Assert.True(PassRule.IsPass(0, ok));
    }

    [Fact]
    public void PassRule_DeviceOfflineZeroExit_Fails()
    {
        // 板子不在下载态：工具照样 exit 0（已发生的假 PASS 事故形态）
        const string offline = "Device offline, only package the file.";
        Assert.False(PassRule.IsPass(0, offline));
    }

    [Fact]
    public void PassRule_NonZeroExit_FailsEvenWithEvidence()
    {
        Assert.False(PassRule.IsPass(1, "Write block 0x0 ...\r\nerror"));
        Assert.False(PassRule.IsPass(-1, "Write block 0x0 ..."));
    }

    // ================= bat 横幅解析（包显示名） =================

    [Theory]
    [InlineData("rem  Firmware : v1.0.1 (git 6a2fdc8), packaged 2026-10-16", "v1.0.1 (6a2fdc8)")]
    [InlineData("rem  Firmware : v1.0.2-rc.2 (git 0bee553), built 2026-10-22", "v1.0.2-rc.2 (0bee553)")]
    [InlineData("title R1D One-Key Flash\r\nrem  Firmware : v1.0.1-rc.1 (git 8f1d122), built 2026-10-21", "v1.0.1-rc.1 (8f1d122)")]
    public void BatBanner_RealBanners_Parse(string batText, string expected) =>
        Assert.Equal(expected, BatBanner.Parse(batText));

    [Theory]
    [InlineData("rem no firmware info here")]
    [InlineData("")]
    public void BatBanner_Garbage_ReturnsNull(string batText) =>
        Assert.Null(BatBanner.Parse(batText));

    // ================= 包动态扫描（01 号 P0-1：不写死清单） =================

    [Fact]
    public void Scan_DynamicFolders_FiltersIncompletePackages()
    {
        var root = NewTempDir();
        try
        {
            // 完整包（带横幅）
            MakePackage(root, "一键烧录脚本", banner: "rem  Firmware : v1.0.1 (git 6a2fdc8), packaged 2026-10-16");
            // 缺 isd_download.exe 的半拷贝包 → 不入列表
            var broken = Path.Combine(root, "一键烧录脚本-deadbee");
            Directory.CreateDirectory(Path.Combine(broken, "flash"));
            File.WriteAllText(Path.Combine(broken, "flash", "isd_config.ini"), "ini");
            // 完整但无横幅 → 显示名回退文件夹名
            MakePackage(root, "一键烧录脚本-8f1d122", banner: null);

            var pkgs = PackageScanner.Scan(root);

            Assert.Equal(2, pkgs.Count);   // 排序按文件夹名："一键烧录脚本" < "一键烧录脚本-8f1d122"
            Assert.Equal("v1.0.1 (6a2fdc8)", pkgs[0].DisplayName);
            Assert.Equal("一键烧录脚本-8f1d122", pkgs[1].DisplayName);   // 横幅缺失回退
            Assert.EndsWith(@"flash", pkgs[0].FlashDir);
            // MD5SUMS 在包根（与 bat 同级，01 号 §2.1），不在 flash\ 里——路径写错 = 全包误报"缺 MD5SUMS"
            Assert.Equal(Path.Combine(pkgs[0].Directory, "MD5SUMS.txt"), pkgs[0].Md5SumsPath);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* 清理失败不影响判定 */ }
        }
    }

    [Fact]
    public void Scan_MissingRoot_ReturnsEmpty() =>
        Assert.Empty(PackageScanner.Scan(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));

    // ================= MD5 自检（01 号 P1-8：不一致阻止烧录） =================

    [Fact]
    public void Md5_Verify_GoodAndTamperedAndMissing()
    {
        var flash = NewTempDir();
        try
        {
            var appPath = Path.Combine(flash, "app.bin");
            File.WriteAllText(appPath, "hello app");
            var goodHash = Md5Of(appPath);

            // app.bin 给错哈希（=篡改形态）→ 报不符；uboot.boot 列出但不存在 → 报缺失
            var sums = $"{new string('f', 32)} *app.bin\n{new string('0', 32)} *uboot.boot\n";
            var report = Md5Checker.Verify(flash, sums);

            Assert.False(report.Ok);
            Assert.Contains(report.Problems, p => p.Contains("app.bin") && p.Contains("不符"));
            Assert.Contains(report.Problems, p => p.Contains("uboot.boot") && p.Contains("缺失"));

            var okReport = Md5Checker.Verify(flash, $"{goodHash} *app.bin\n");
            Assert.True(okReport.Ok);
        }
        finally
        {
            try { Directory.Delete(flash, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Md5_Verify_IgnoresVolatileArtifacts_StillChecksRealFirmware()
    {
        var flash = NewTempDir();
        try
        {
            var appPath = Path.Combine(flash, "app.bin");
            File.WriteAllText(appPath, "hello app");
            var goodAppHash = Md5Of(appPath);
            // 0bee553 实际形态：清单把运行期产物 jl_isd.fw 也列了，哈希已被首次烧录作废
            File.WriteAllText(Path.Combine(flash, "jl_isd.fw"), "每次运行被 isd_download 重写");

            // jl_isd.fw 错哈希不罚（跳过比对），真固件 app.bin 哈希对 → 放行
            var sums = $"{goodAppHash} *app.bin\n{new string('f', 32)} *jl_isd.fw\n";
            Assert.True(Md5Checker.Verify(flash, sums).Ok);

            // 真固件被换仍必须拦
            Assert.False(Md5Checker.Verify(flash, $"{new string('0', 32)} *app.bin\n").Ok);
        }
        finally
        {
            try { Directory.Delete(flash, recursive: true); } catch { }
        }
    }

    // ================= 烧录日志落盘（10-23 指挥官反馈：排障证据要留本地 log\） =================

    [Fact]
    public void FlashLog_DailyFile_AppendsWithTimestampPrefix()
    {
        var root = NewTempDir();
        try
        {
            using (var log = FlashLog.OpenDaily(root))
                log.WriteLine("第一行");
            using (var log = FlashLog.OpenDaily(root))
                log.WriteLine("第二行");

            var path = Path.Combine(root, "log", "DongleFlash_" + DateTime.Now.ToString("yyyyMMdd") + ".log");
            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.True(System.Text.RegularExpressions.Regex.IsMatch(lines[0], @"^\d{2}:\d{2}:\d{2} 第一行$"),
                $"应有时分秒前缀，实际：{lines[0]}");
            Assert.True(System.Text.RegularExpressions.Regex.IsMatch(lines[1], @"^\d{2}:\d{2}:\d{2} 第二行$"),
                $"应有时分秒前缀，实际：{lines[1]}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void FlashLog_UnwritableBase_ReturnsNullQuietly()
    {
        var notADir = Path.Combine(NewTempDir(), "占位文件不是目录");
        File.WriteAllText(notADir, "x");
        Assert.Null(FlashLog.OpenDaily(notADir));   // 建目录失败 → 降级返 null，绝不拖垮烧录
    }

    // ================= 真实材料冒烟（材料在位才生效，离线开发机可跳过） =================

    [Fact]
    public void Scan_RealR1DMaterials_IfPresent()
    {
        var materialsRoot = FindMaterialsRoot();
        if (materialsRoot == null)
            return;   // 交付环境无材料树，静默通过

        var pkgs = PackageScanner.Scan(materialsRoot);
        Assert.True(pkgs.Count >= 3, $"真实 R1D 材料应扫到 ≥3 包，实际 {pkgs.Count}");
        Assert.All(pkgs, p =>
        {
            Assert.True(File.Exists(Path.Combine(p.FlashDir, "isd_download.exe")), $"{p.FolderName} 缺 isd_download.exe");
            Assert.True(File.Exists(p.BatPath), $"{p.FolderName} 缺 一键烧录.bat");
            Assert.True(File.Exists(p.Md5SumsPath), $"{p.FolderName} 包根缺 MD5SUMS.txt");
        });
        Assert.Contains(pkgs, p => p.DisplayName == "v1.0.1 (6a2fdc8)");
        Assert.Contains(pkgs, p => p.DisplayName == "v1.0.2-rc.2 (0bee553)");
    }

    // ================= 杂项 =================

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dongleflash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void MakePackage(string root, string folder, string? banner)
    {
        var dir = Path.Combine(root, folder);
        Directory.CreateDirectory(Path.Combine(dir, "flash"));
        File.WriteAllText(Path.Combine(dir, "flash", "isd_download.exe"), "fake");
        File.WriteAllText(Path.Combine(dir, "flash", "isd_config.ini"), "ini");
        var bat = "title fake\r\n" + (banner != null ? banner + "\r\n" : "");
        File.WriteAllText(Path.Combine(dir, "一键烧录.bat"), bat);
    }

    private static string Md5Of(string path)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        using var fs = File.OpenRead(path);
        return BitConverter.ToString(md5.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
    }

    /// <summary>材料权威源=produce\R1D（10-22 起，custom\生产使用\R1D 已删）；custom 作历史回退。</summary>
    private static string? FindMaterialsRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var produce = Path.Combine(dir.FullName, "output", "produce", "R1D");
            if (Directory.Exists(produce))
                return produce;
            var custom = Path.Combine(dir.FullName, "output", "custom", "生产使用", "R1D");
            if (Directory.Exists(custom))
                return custom;
        }
        return null;
    }
}
