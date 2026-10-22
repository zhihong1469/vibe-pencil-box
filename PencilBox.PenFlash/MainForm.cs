// <copyright file="MainForm.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace PencilBox.PenFlash;

/// <summary>
/// 产线操作面：固件下拉（出货版默认）+ 串口勾选（多候选强制手选）+ 大按钮 +
/// 每口状态行 + PASS/FAIL 大字 + 日志折叠 + 计数。1366×768 适配，10 米外可辨。
/// </summary>
public sealed class MainForm : Form
{
    private readonly PenFlashEngine _engine;

    private ComboBox _fwCombo = null!;
    private Button _fwRefresh = null!;
    private Label _shippingLabel = null!;
    private CheckedListBox _portList = null!;
    private Button _portRefresh = null!;
    private Button _startButton = null!;
    private CheckBox _parallelCheck = null!;
    private CheckBox _probeCheck = null!;
    private Label _resultLabel = null!;
    private ListView _portView = null!;
    private TextBox _logBox = null!;
    private Button _logToggle = null!;
    private Label _countLabel = null!;
    private Button _driverButton = null!;

    private string? _shippingVersion;
    private CancellationTokenSource? _cts;
    private volatile bool _busy;
    private int _passCount;
    private int _failCount;
    private bool _lastBatchAllPass;

    public MainForm()
    {
        PenFlashEngine engine;
        try
        {
            engine = new PenFlashEngine();
        }
        catch (Exception ex)
        {
            // 材料缺失也要给出可操作提示（产线环境自查）
            MessageBox.Show(ex.Message, "PenFlash — 材料缺失", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            throw;
        }
        _engine = engine;

        Text = "PenFlash — R1P 笔端产线烧录";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(920, 660);
        Size = new Size(960, 700);
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildUi();
        Load += (_, _) => { RefreshFirmwares(); RefreshPorts(); };
    }

    private void BuildUi()
    {
        var materialLabel = new Label
        {
            Text = $"材料：{_engine.MaterialsRoot}",
            AutoEllipsis = true,
            Dock = DockStyle.Top,
            Height = 24,
            ForeColor = Color.DimGray,
        };

        // ---- 固件组 ----
        var fwGroup = new GroupBox { Text = "固件（扫描 固件\\*.bin）", Left = 12, Top = 30, Width = 520, Height = 96 };
        _fwCombo = new ComboBox { Left = 12, Top = 24, Width = 380, DropDownStyle = ComboBoxStyle.DropDownList, Font = new Font("Microsoft YaHei UI", 10.5F) };
        _fwRefresh = new Button { Left = 400, Top = 23, Width = 100, Height = 30, Text = "刷新" };
        _shippingLabel = new Label { Left = 12, Top = 62, Width = 480, Height = 22, ForeColor = Color.DarkGreen };
        _fwRefresh.Click += (_, _) => RefreshFirmwares();
        fwGroup.Controls.AddRange(new Control[] { _fwCombo, _fwRefresh, _shippingLabel });

        // ---- 串口组 ----
        var portGroup = new GroupBox { Text = "串口（CH340 治具，VID 1A86:7523）", Left = 544, Top = 30, Width = 390, Height = 96 };
        _portList = new CheckedListBox { Left = 12, Top = 20, Width = 200, Height = 66, CheckOnClick = true };
        _portRefresh = new Button { Left = 224, Top = 20, Width = 100, Height = 30, Text = "刷新" };
        var portTip = new Label { Left = 224, Top = 56, Width = 160, Height = 30, ForeColor = Color.DimGray, Text = "多个候选必须勾选，\r\n不会自动猜口" };
        _portRefresh.Click += (_, _) => RefreshPorts();
        portGroup.Controls.AddRange(new Control[] { _portList, _portRefresh, portTip });

        // ---- 大按钮 + 选项 ----
        _startButton = new Button
        {
            Left = 12, Top = 138, Width = 560, Height = 68,
            Text = "开始烧录",
            Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold),
            BackColor = Color.FromArgb(0, 120, 60),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
        };
        _startButton.Click += OnStartClick;
        _parallelCheck = new CheckBox { Left = 588, Top = 144, Width = 330, Text = "并发（实验：OmBleIsp 多实例未验证，默认排队轮烧）", Checked = false };
        _probeCheck = new CheckBox { Left = 588, Top = 172, Width = 330, Text = "烧后抽检（同口 1M 波特率读开机版本 8 秒）" };

        // ---- 结果大字 ----
        _resultLabel = new Label
        {
            Left = 12, Top = 214, Width = 922, Height = 88,
            Text = "待机",
            Font = new Font("Microsoft YaHei UI", 40F, FontStyle.Bold),
            ForeColor = Color.DimGray,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.WhiteSmoke,
        };

        // ---- 每口状态 ----
        var stateGroup = new GroupBox { Text = "状态（每口一行）", Left = 12, Top = 310, Width = 922, Height = 110 };
        _portView = new ListView
        {
            Left = 10, Top = 18, Width = 898, Height = 82,
            View = View.Details,
            FullRowSelect = true,
            Font = new Font("Microsoft YaHei UI", 11F),
        };
        _portView.Columns.Add("串口", 120);
        _portView.Columns.Add("阶段", 260);
        _portView.Columns.Add("结果", 500);
        stateGroup.Controls.Add(_portView);

        // ---- 日志（可折叠）----
        _logBox = new TextBox
        {
            Left = 12, Top = 452, Width = 922, Height = 150,
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 9F),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        };
        _logToggle = new Button { Left = 850, Top = 428, Width = 84, Height = 24, Text = "收起 ▲", Anchor = AnchorStyles.Top | AnchorStyles.Right };
        _logToggle.Click += (_, _) =>
        {
            var expanded = _logBox.Height > 40;
            _logBox.Height = expanded ? 32 : 150;
            _logToggle.Text = expanded ? "展开 ▼" : "收起 ▲";
        };

        // ---- 底部：计数 + 驱动 ----
        _countLabel = new Label { Left = 12, Top = 612, Width = 400, Height = 26, Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold), Text = "本班：PASS 0   FAIL 0", Anchor = AnchorStyles.Left | AnchorStyles.Bottom };
        _driverButton = new Button { Left = 830, Top = 608, Width = 104, Height = 30, Text = "驱动安装…", Anchor = AnchorStyles.Right | AnchorStyles.Bottom };
        _driverButton.Click += (_, _) => OpenDriverFolder();

        Controls.AddRange(new Control[]
        {
            _countLabel, _driverButton, _logToggle, _logBox,
            stateGroup, _resultLabel, _startButton, _parallelCheck, _probeCheck,
            portGroup, fwGroup, materialLabel,
        });
    }

    // ================= 数据刷新 =================

    private void RefreshFirmwares()
    {
        _fwCombo.Items.Clear();
        List<FirmwareEntry> items;
        try { items = _engine.ScanFirmwares(); }
        catch (Exception ex) { LogLine($"[ERR] 固件扫描失败：{ex.Message}"); return; }

        _shippingVersion = _engine.ReadShippingVersion();
        var defaultPath = PenFlashCore.SelectShippingFirmware(items, _shippingVersion);
        _shippingLabel.Text = _shippingVersion is null
            ? "出货版口径未配置（固件\\一期出货使用的*.txt 为空/缺失）——请手动选固件"
            : $"出货版：{_shippingVersion}" + (defaultPath is null ? "（列表未唯一匹配，请手动选）" : "（已默认选中）");

        foreach (var f in items)
            _fwCombo.Items.Add(new ComboItem(f.Path, $"{PenFlashCore.DescribeFirmware(f.FileName)}  —  {f.FileName}"));
        if (_fwCombo.Items.Count == 0)
            LogLine("[WARN] 固件目录没有 *.bin");
        else if (defaultPath is not null)
        {
            var idx = items.FindIndex(f => f.Path == defaultPath);
            if (idx >= 0) _fwCombo.SelectedIndex = idx;
        }
        else if (_fwCombo.Items.Count > 0)
            _fwCombo.SelectedIndex = 0;
    }

    private void RefreshPorts()
    {
        var checkedNow = _portList.CheckedItems.OfType<PortItem>().Select(p => p.Port).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _portList.Items.Clear();
        List<string> ports;
        try { ports = _engine.DetectCh340Ports(); }
        catch (Exception ex) { LogLine($"[ERR] 串口探测失败：{ex.Message}"); return; }

        foreach (var p in ports)
            _portList.Items.Add(new PortItem(p), checkedNow.Contains(p));
        // 唯一候选才自动选中；多候选不猜（01 号 §2.3-5）
        if (checkedNow.Count == 0 && PenFlashCore.PickDefaultPort(ports) is { } auto)
            _portList.SetItemChecked(ports.IndexOf(auto), true);
        if (ports.Count == 0)
            LogLine("[WARN] 未发现 CH340 治具（VID:PID=1A86:7523）——插好治具或安装驱动");
    }

    // ================= 烧录流程 =================

    private async void OnStartClick(object? sender, EventArgs e)
    {
        if (_busy)
        {
            // 烧录中点击 = 取消（按钮此时显示「取消」）
            _cts?.Cancel();
            return;
        }

        var firmware = (_fwCombo.SelectedItem as ComboItem)?.Path;
        var ports = _portList.CheckedItems.OfType<PortItem>().Select(p => p.Port).ToList();
        if (firmware is null) { MessageBox.Show("请先选固件", "PenFlash"); return; }
        if (ports.Count == 0) { MessageBox.Show("请先勾选烧录串口（多候选必须明确勾选）", "PenFlash"); return; }

        _busy = true;
        _cts = new CancellationTokenSource();
        _startButton.Text = "烧录中…（点击取消）";
        _startButton.BackColor = Color.FromArgb(160, 80, 0);
        SetPortRows(ports);
        LogLine($"===== 开始：{Path.GetFileName(firmware)} → {string.Join(", ", ports)}（{(_parallelCheck.Checked ? "并发" : "排队")}模式）=====");

        var results = new Dictionary<string, PortFlashSession>();
        try
        {
            var sessions = ports.Select(p => new PortFlashSession(_engine, p, firmware)).ToList();
            foreach (var s in sessions)
            {
                var session = s;
                s.Log += (_, line) => Ui(() => LogLine(line));
                s.PhaseChanged += (_, _) => Ui(() => UpdateRow(session));
            }

            if (_parallelCheck.Checked)
                await Task.WhenAll(sessions.Select(s => s.RunAsync(_cts.Token))).ConfigureAwait(true);
            else
                foreach (var s in sessions)
                    await s.RunAsync(_cts.Token).ConfigureAwait(true);

            foreach (var s in sessions)
                results[s.Port] = s;
        }
        catch (OperationCanceledException) { /* 会话各自收 FAILED */ }

        var pass = results.Values.Count(s => s.Passed == true);
        var fail = results.Values.Count(s => s.Passed == false);
        _passCount += pass;
        _failCount += fail;
        _lastBatchAllPass = fail == 0 && results.Count > 0;
        _countLabel.Text = $"本班：PASS {_passCount}   FAIL {_failCount}";

        if (results.Count == 1)
        {
            var only = results.Values.Single();
            _resultLabel.Text = only.Passed == true ? "PASS" : "FAIL";
            _resultLabel.ForeColor = only.Passed == true ? Color.FromArgb(0, 140, 0) : Color.FromArgb(200, 0, 0);
        }
        else
        {
            _resultLabel.Text = fail == 0 ? $"PASS（{pass} 支全过）" : $"FAIL {fail} / {results.Count}";
            _resultLabel.ForeColor = fail == 0 ? Color.FromArgb(0, 140, 0) : Color.FromArgb(200, 0, 0);
        }

        LogLine($"===== 本轮结束：PASS {pass}，FAIL {fail} =====");

        // 抽检（可选，逐口；口已空闲）：1M 波特率读开机横幅比对所选固件版本
        if (_probeCheck.Checked && pass > 0)
            await RunProbeAsync(ports, firmware).ConfigureAwait(true);

        _busy = false;
        _cts.Dispose();
        _cts = null;
        // 连续烧录（01 号 P0-5）：PASS 后换下一支笔直接再点；不做倒计时自动开烧（防笔没放好）
        _startButton.Text = _lastBatchAllPass ? "继续下一支" : "重试";
        _startButton.BackColor = _lastBatchAllPass ? Color.FromArgb(0, 120, 60) : Color.FromArgb(160, 80, 0);
    }

    private async Task RunProbeAsync(List<string> ports, string firmware)
    {
        var expected = PenFlashCore.ParseFirmwareVersion(Path.GetFileName(firmware));
        foreach (var p in ports)
        {
            LogLine($"[抽检] {p} 监听开机日志 8 秒（@{PenFlashCore.MonitorBaudrate}）…");
            var text = await PostFlashProbe.ListenAsync(p, 8, CancellationToken.None).ConfigureAwait(true);
            foreach (var line in text.Split('\n'))
                if (line.Trim().Length > 0) LogLine($"    {p} | {line.TrimEnd('\r')}");
            var banner = PostFlashProbe.ExtractBannerVersion(text);
            if (banner is null)
                LogLine($"[抽检] {p} 未抓到版本横幅（笔可能没开口日志/已休眠——抽检是可选复核，不影响 PASS 判定）");
            else if (expected is not null && !string.Equals(banner, expected, StringComparison.OrdinalIgnoreCase))
                LogLine($"[抽检] {p} [WARN] 横幅版本 {banner} ≠ 所选固件 {expected}——请复核固件选择！");
            else
                LogLine($"[抽检] {p} 横幅版本 {banner} = 所选固件 ✓");
        }
    }

    private void OpenDriverFolder()
    {
        var dir = Path.Combine(_engine.ToolDir, "驱动");
        if (!Directory.Exists(dir))
        {
            MessageBox.Show($"找不到驱动目录：{dir}\n（人工到材料目录 OmBleISP_cmd_tool\\驱动 下解压 x64.rar 双击安装）", "PenFlash");
            return;
        }
        // rar 解压依赖本机解压器，不做静默解包（01 号已背书人工安装可接受）——直接打开目录引导
        System.Diagnostics.Process.Start("explorer.exe", $"\"{dir}\"");
        LogLine("[驱动] 已打开驱动目录：解压 x64.rar 后双击安装 CH340 驱动");
    }

    // ================= UI 帮手 =================

    private void SetPortRows(List<string> ports)
    {
        _portView.Items.Clear();
        foreach (var p in ports)
        {
            var item = new ListViewItem(p) { Tag = p };
            item.SubItems.Add("待烧");
            item.SubItems.Add("");
            _portView.Items.Add(item);
        }
    }

    private void UpdateRow(PortFlashSession s)
    {
        var item = _portView.Items.OfType<ListViewItem>().FirstOrDefault(i => (string?)i.Tag == s.Port);
        if (item is null) return;
        if (s.Passed is { } passed)
        {
            item.SubItems[1].Text = "结束";
            item.SubItems[2].Text = s.Summary;
            item.SubItems[2].ForeColor = passed ? Color.FromArgb(0, 140, 0) : Color.FromArgb(200, 0, 0);
        }
        else
        {
            item.SubItems[1].Text = s.Phase switch
            {
                FlashPhase.EnterIsp => "进 ISP",
                FlashPhase.Burn => "烧录+校验",
                FlashPhase.Run => "起跑",
                FlashPhase.CleanReset => "干净复位",
                _ => s.Phase.ToString(),
            };
        }
    }

    private void Ui(Action action)
    {
        if (IsHandleCreated && !IsDisposed)
            BeginInvoke(action);
    }

    private void LogLine(string line)
    {
        _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
    }

    private sealed record ComboItem(string Path, string Display)
    {
        public override string ToString() => Display;
    }

    private sealed record PortItem(string Port)
    {
        public override string ToString() => Port;
    }
}
