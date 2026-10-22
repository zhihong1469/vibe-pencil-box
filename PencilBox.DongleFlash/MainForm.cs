// <copyright file="MainForm.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Diagnostics;
using System.Windows.Forms;

namespace PencilBox.DongleFlash;

/// <summary>
/// R1D 产线烧录主窗（01 号 §四草图）：大按钮大字号，PASS 绿 / FAIL 红。
/// 产线离线绿色工具——只读本程序所在目录下的 `一键烧录脚本*` 包，不写注册表/系统目录。
/// </summary>
internal sealed class MainForm : Form
{
    private static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;

    private readonly ComboBox _pkgBox = new();
    private readonly Button _refreshBtn = new();
    private readonly Label _md5Label = new();
    private readonly Button _flashBtn = new();
    private readonly Button _cancelBtn = new();
    private readonly Label _statusLabel = new();
    private readonly Label _resultLabel = new();
    private readonly Label _nextHintLabel = new();
    private readonly CheckBox _logToggle = new();
    private readonly TextBox _logBox = new();
    private readonly Label _countLabel = new();
    private readonly Button _driverBtn = new();
    private readonly Panel _driverPanel = new();
    private readonly Button _serdrvBtn = new();
    private readonly Button _vcredistBtn = new();

    private readonly FlashRunner _runner = new();
    private readonly List<FlashPackage> _packages = new();
    private Md5Report? _md5;
    private CancellationTokenSource? _cts;
    private int _passCount, _failCount;
    private bool _flashing;

    public MainForm()
    {
        Text = "R1D Dongle 产线烧录";
        Font = new System.Drawing.Font("Microsoft YaHei UI", 9f);
        ClientSize = new System.Drawing.Size(700, 560);
        MinimumSize = new System.Drawing.Size(720, 600);
        StartPosition = FormStartPosition.CenterScreen;
        // 可缩放+可最大化（指挥官 10-22 目验反馈）：后续一拖多时中部结果区改槽位行，布局已全锚定
        MaximizeBox = true;

        // ---- 固件包行 ----
        Controls.Add(new Label { Text = "固件版本:", Location = new System.Drawing.Point(15, 18), AutoSize = true });
        _pkgBox.SetBounds(90, 14, 420, 26);
        _pkgBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _pkgBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _pkgBox.SelectedIndexChanged += async (_, _) => await OnPackageSelectedAsync();
        Controls.Add(_pkgBox);

        _refreshBtn.SetBounds(520, 13, 66, 28);
        _refreshBtn.Text = "刷新";
        _refreshBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _refreshBtn.Click += (_, _) => ScanAndFill();
        Controls.Add(_refreshBtn);

        _md5Label.SetBounds(594, 18, 96, 22);
        _md5Label.AutoSize = false;
        _md5Label.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
        _md5Label.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        Controls.Add(_md5Label);

        // ---- 大按钮 / 取消 ----
        _flashBtn.SetBounds(90, 55, 494, 72);
        _flashBtn.Text = "开始烧录";
        _flashBtn.Font = new System.Drawing.Font("Microsoft YaHei UI", 16f, System.Drawing.FontStyle.Bold);
        _flashBtn.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _flashBtn.Enabled = false;
        _flashBtn.Click += async (_, _) => await OnFlashAsync();
        Controls.Add(_flashBtn);

        _cancelBtn.SetBounds(594, 55, 96, 72);
        _cancelBtn.Text = "取消";
        _cancelBtn.Font = new System.Drawing.Font("Microsoft YaHei UI", 12f, System.Drawing.FontStyle.Bold);
        _cancelBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _cancelBtn.Visible = false;
        _cancelBtn.Click += (_, _) => _cts?.Cancel();
        Controls.Add(_cancelBtn);

        // ---- 状态 / 结果大字 ----
        _statusLabel.SetBounds(15, 140, 675, 26);
        _statusLabel.Font = new System.Drawing.Font("Microsoft YaHei UI", 11f);
        _statusLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _statusLabel.Text = "正在扫描烧录包…";
        Controls.Add(_statusLabel);

        _resultLabel.SetBounds(15, 172, 675, 108);
        _resultLabel.Font = new System.Drawing.Font("Microsoft YaHei UI", 48f, System.Drawing.FontStyle.Bold);
        _resultLabel.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
        _resultLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(_resultLabel);

        _nextHintLabel.SetBounds(15, 284, 675, 22);
        _nextHintLabel.ForeColor = System.Drawing.Color.Gray;
        _nextHintLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(_nextHintLabel);

        // ---- 日志 ----
        _logToggle.SetBounds(15, 312, 120, 24);
        _logToggle.Text = "日志 ▸";
        _logToggle.CheckedChanged += (_, _) =>
        {
            _logBox.Visible = _logToggle.Checked;
            _logToggle.Text = _logToggle.Checked ? "日志 ▾" : "日志 ▸";
        };
        Controls.Add(_logToggle);

        _logBox.SetBounds(15, 338, 675, 158);
        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        _logBox.ScrollBars = ScrollBars.Both;
        _logBox.WordWrap = false;
        _logBox.Font = new System.Drawing.Font("Consolas", 9f);
        _logBox.Visible = false;
        _logBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(_logBox);

        // ---- 底部：计数 + 驱动安装 ----
        _countLabel.SetBounds(15, 506, 300, 24);
        _countLabel.Text = CountText;
        _countLabel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        Controls.Add(_countLabel);

        _driverBtn.SetBounds(588, 504, 102, 28);
        _driverBtn.Text = "驱动安装 ▾";
        _driverBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _driverBtn.Click += (_, _) =>
        {
            _driverPanel.Visible = !_driverPanel.Visible;
            if (_driverPanel.Visible)
                _driverPanel.BringToFront();   // 展开态日志框加在面板之前（z 序在上），不置顶会被压住=点了"没反应"
            _driverBtn.Text = _driverPanel.Visible ? "驱动安装 ▴" : "驱动安装 ▾";
        };
        Controls.Add(_driverBtn);

        _driverPanel.SetBounds(440, 466, 250, 36);
        _driverPanel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _driverPanel.Visible = false;
        _serdrvBtn.SetBounds(0, 0, 122, 30);
        _serdrvBtn.Text = "安装串口驱动";
        _serdrvBtn.Click += (_, _) => LaunchTool(Path.Combine(BaseDir, "烧录依赖", "驱动", "serdrv.exe"));
        _driverPanel.Controls.Add(_serdrvBtn);
        _vcredistBtn.SetBounds(128, 0, 122, 30);
        _vcredistBtn.Text = "安装运行库";
        _vcredistBtn.Click += (_, _) => LaunchTool(Path.Combine(BaseDir, "烧录依赖", "运行库", "vcredist_x86.exe"));
        _driverPanel.Controls.Add(_vcredistBtn);
        Controls.Add(_driverPanel);

        Load += (_, _) => ScanAndFill();
    }

    private string CountText => $"本班: PASS {_passCount}   FAIL {_failCount}";

    // ================= 包扫描与 MD5 =================

    private void ScanAndFill()
    {
        _packages.Clear();
        _pkgBox.Items.Clear();
        _md5 = null;

        _packages.AddRange(PackageScanner.Scan(BaseDir));
        foreach (var p in _packages)
            _pkgBox.Items.Add(p.DisplayName);

        if (_packages.Count == 0)
        {
            SetStatus("未找到烧录包：请把本程序放在 R1D 包根目录（与『一键烧录脚本*』文件夹同级）", true);
            UpdateFlashButton();
            return;
        }

        _pkgBox.SelectedIndex = 0;   // 触发 OnPackageSelectedAsync → MD5 校验
    }

    private FlashPackage? Selected => _pkgBox.SelectedIndex >= 0 && _pkgBox.SelectedIndex < _packages.Count
        ? _packages[_pkgBox.SelectedIndex] : null;

    private async Task OnPackageSelectedAsync()
    {
        var pkg = Selected;
        _md5 = null;
        _md5Label.Text = "";
        _resultLabel.Text = "";
        _nextHintLabel.Text = "";
        UpdateFlashButton();

        if (pkg == null)
            return;

        SetStatus($"已选 {pkg.DisplayName}，MD5 自检中…");
        if (!File.Exists(pkg.Md5SumsPath))
        {
            _md5 = new Md5Report(false, new[] { "包内缺 MD5SUMS.txt，无法自检（阻止烧录，请补齐包材料）" });
        }
        else
        {
            var text = await Task.Run(() => File.ReadAllText(pkg.Md5SumsPath)).ConfigureAwait(true);
            var flashDir = pkg.FlashDir;
            _md5 = await Task.Run(() => Md5Checker.Verify(flashDir, text)).ConfigureAwait(true);
        }

        // 选中项可能在 await 期间被切换：只认当前选中
        if (!ReferenceEquals(Selected, pkg))
            return;

        _md5Label.Text = _md5!.Ok ? "✓ MD5" : "✗ MD5";
        _md5Label.ForeColor = _md5.Ok ? System.Drawing.Color.Green : System.Drawing.Color.Red;
        if (!_md5.Ok)
        {
            AppendLog($"[MD5] {pkg.DisplayName} 自检不过：");
            foreach (var problem in _md5.Problems)
                AppendLog($"  - {problem}");
            _logToggle.Checked = true;
            SetStatus("MD5 自检不过，已阻止烧录——包材料异常，找工程师处理", true);
        }
        else
        {
            SetStatus($"就绪：{pkg.DisplayName}（放入板子 USB，点【开始烧录】；板上已在线则先拔下快插一次触发下载态）");
        }
        UpdateFlashButton();
    }

    private void UpdateFlashButton()
    {
        if (_flashing)
            return;
        _flashBtn.Enabled = Selected != null && _md5 is { Ok: true };
    }

    // ================= 烧录 =================

    private async Task OnFlashAsync()
    {
        var pkg = Selected;
        if (pkg == null || _flashing || _md5 is not { Ok: true })
            return;

        _flashing = true;
        _cts = new CancellationTokenSource();
        SetBusy(true);
        _resultLabel.Text = "";
        _nextHintLabel.Text = "";
        _logToggle.Checked = true;
        AppendLog("");
        AppendLog($"==== 开始烧录 {pkg.DisplayName} ====");
        AppendLog($"run: isd_download.exe {FlashRunner.Arguments}");
        AppendLog($"workdir: {pkg.FlashDir}");
        SetStatus("烧录中（30-60s）… 未插板则显示等待板子插入/快插，勿断电勿拔板");

        var result = await _runner.RunAsync(pkg.FlashDir,
            line => BeginInvoke(new Action(() => AppendLog(line))), _cts.Token).ConfigureAwait(true);

        _flashing = false;
        SetBusy(false);

        if (result.Pass)
        {
            _passCount++;
            _resultLabel.ForeColor = System.Drawing.Color.Green;
            _resultLabel.Text = "PASS";
            _flashBtn.Text = "开始下一块";
            _nextHintLabel.Text = "烧录成功，设备已复位进应用。放入下一块板（已在线则拔下快插），点【开始下一块】。";
            SetStatus($"PASS（{result.Duration.TotalSeconds:0} 秒）");
            AppendLog("[PASS] Flash OK. Device rebooted into app.");
        }
        else if (_cts.IsCancellationRequested)
        {
            // 手动中止：板子状态未知但未做任何 PASS 声明，不计入对账计数
            _resultLabel.ForeColor = System.Drawing.Color.OrangeRed;
            _resultLabel.Text = "已取消";
            _flashBtn.Text = "重试";
            _nextHintLabel.Text = "已手动取消（不计入本班计数）。快插板子 USB 后点【重试】。";
            SetStatus("已取消");
            AppendLog("[CANCEL] 手动取消");
        }
        else
        {
            _failCount++;
            _resultLabel.ForeColor = System.Drawing.Color.Red;
            _resultLabel.Text = "FAIL";
            _flashBtn.Text = "重试";
            _nextHintLabel.Text = "烧录未完成（板子不在下载态或写入证据缺失）。快插板子 USB，等 2 秒后点【重试】；反复失败找工程师。";
            SetStatus("FAIL——展开日志看原因（如 Device offline = 板子不在下载态）", true);
            AppendLog($"[FAIL] 退出码 {result.ExitCode}，无写入证据（详见上方完整日志）");
            _logToggle.Checked = true;   // FAIL 必展开日志（01 号验收 5）
        }

        _countLabel.Text = CountText;
        UpdateFlashButton();
    }

    private void SetBusy(bool busy)
    {
        _pkgBox.Enabled = !busy;
        _refreshBtn.Enabled = !busy;
        _driverBtn.Enabled = !busy;
        _flashBtn.Visible = !busy;
        _cancelBtn.Visible = busy;
        if (!busy)
            _flashBtn.Text = "开始烧录";
    }

    private void SetStatus(string text, bool warn = false) =>
        _statusLabel.Text = warn ? "⚠ " + text : text;

    // ================= 杂项 =================

    private void LaunchTool(string path)
    {
        if (!File.Exists(path))
        {
            AppendLog($"[驱动] 未找到 {path}（包材料不齐，请人工双击安装）");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            AppendLog($"[驱动] 已调起：{path}");
        }
        catch (Exception ex)
        {
            AppendLog($"[驱动] 调起失败：{ex.Message}（请人工双击安装）");
        }
    }

    private void AppendLog(string line)
    {
        _logBox.AppendText(line + Environment.NewLine);
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
    }
}
