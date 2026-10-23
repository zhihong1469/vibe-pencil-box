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
/// 产线操作面（版式对齐 ota-gui 多支同升，指挥官 10-22 目验反馈）：
/// 顶层选固件（一对多的"一"）+ 阶段条（状态+本班计数+驱动）+ 每口一行（勾选要烧的口）+ 底部日志。
/// 第四轮目验定版：控件尺寸全线统一（按钮忽大忽小砍）、大字横幅整行砍（状态并入阶段条左格）。
/// Dock 装配：Fill 最先加入，随后 Bottom/Top（后加者先布局）。
/// </summary>
internal sealed class MainForm : Form
{
    private readonly PenFlashEngine _engine;

    private readonly ComboBox _fwCombo;
    private readonly Button _refreshFwBtn;
    private readonly Button _refreshPortBtn;
    private readonly RadioButton _armRadio;
    private readonly RadioButton _espRadio;

    /// <summary>治具烧录开关档位（操作口径：选错一般也能烧，但两档流程提示/失败三查按此对齐——使用说明）。</summary>
    private string Position => _espRadio.Checked ? "ESP" : "ARM";
    private readonly Button _startBtn;
    private readonly Button _cancelBtn;
    private readonly CheckBox _probeCheck;
    private readonly Label _infoLabel;        // 出货版徽标/材料提示（dimgrey，同 ota-gui _fwInfo 位）
    private readonly Label _phaseLabel;       // 全局状态（待机/烧录中/本轮 PASS|FAIL，原大字横幅并入此格）
    private readonly Label _countLabel;
    private readonly Button _driverBtn;
    private readonly ListView _list;          // 一口一行（CheckBoxes，同 ota-gui 设备列表）
    private readonly TextBox _logBox;

    private readonly List<string> _logLines = new();
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

        Text = "PenFlash — R1P 笔端产线烧录（ISP 串口·CH340 治具）";
        Font = new Font("Microsoft YaHei UI", 9f);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(880, 600);
        Size = new Size(1000, 720);

        // ---- 顶层：固件选择（一对多的"一"，固件框随窗拉伸）+ 动作按钮 ----
        var top = new Panel { Dock = DockStyle.Top, Height = 96, Padding = new Padding(10, 8, 10, 0) };
        var fwRow = new TableLayoutPanel { Dock = DockStyle.Top, Height = 36 };
        fwRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));        // 固件：
        fwRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));    // 固件框吃掉剩余宽
        fwRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fwRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fwRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));        // 治具档位：
        fwRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));        // ARM
        fwRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));        // ESP
        _fwCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = new Font("Microsoft YaHei UI", 10.5f),
            Dock = DockStyle.Fill,
            Margin = new Padding(3, 5, 3, 0),
        };
        _refreshFwBtn = new Button { Text = "刷新固件", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 3, 3, 0) };
        _refreshFwBtn.Click += (_, _) => RefreshFirmwares();
        _refreshPortBtn = new Button { Text = "刷新串口", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 3, 3, 0) };
        _refreshPortBtn.Click += (_, _) => RefreshPorts();
        _armRadio = new RadioButton { Text = "ARM 档", AutoSize = true, Checked = true, Anchor = AnchorStyles.Left, Margin = new Padding(12, 3, 3, 0) };
        _espRadio = new RadioButton { Text = "ESP 档", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 3, 10, 0) };
        fwRow.Controls.Add(new Label { Text = "固件：", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 0, 0, 0) }, 0, 0);
        fwRow.Controls.Add(_fwCombo, 1, 0);
        fwRow.Controls.Add(_refreshFwBtn, 2, 0);
        fwRow.Controls.Add(_refreshPortBtn, 3, 0);
        fwRow.Controls.Add(new Label { Text = "治具档位：", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(6, 0, 0, 0) }, 4, 0);
        fwRow.Controls.Add(_armRadio, 5, 0);
        fwRow.Controls.Add(_espRadio, 6, 0);

        var actionRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, WrapContents = false };
        _startBtn = new Button
        {
            Text = "开始烧录（勾选口）",
            AutoSize = true,
            BackColor = Color.FromArgb(0, 120, 60),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(3, 3, 6, 0),
        };
        _startBtn.Click += (_, _) => OnStartClick();
        actionRow.Controls.Add(_startBtn);
        _cancelBtn = new Button { Text = "取消", AutoSize = true, Enabled = false };
        _cancelBtn.Click += (_, _) =>
        {
            LogLine("已请求取消（烧录中的口会终止 OmBleIsp 进程并判 FAIL）…");
            _cts?.Cancel();
        };
        actionRow.Controls.Add(_cancelBtn);
        _probeCheck = new CheckBox
        {
            Text = "烧后抽检（1M 读开机版本 8 秒）",
            AutoSize = true,
            Margin = new Padding(6, 8, 0, 0),
        };
        actionRow.Controls.Add(_probeCheck);
        _infoLabel = new Label
        {
            Text = "就绪",
            AutoSize = true,
            Margin = new Padding(12, 10, 0, 0),
            ForeColor = Color.DimGray,
        };
        actionRow.Controls.Add(_infoLabel);
        top.Controls.Add(actionRow);
        top.Controls.Add(fwRow);

        // ---- 阶段条：状态 + 本班计数 + 驱动安装（TableLayoutPanel 根治 Right 停靠互相挤压遮挡）----
        var phaseStrip = new TableLayoutPanel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(12, 3, 10, 0) };
        phaseStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        phaseStrip.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        phaseStrip.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _phaseLabel = new Label { Text = "待机", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty, ForeColor = Color.DimGray };
        _countLabel = new Label
        {
            Text = "本班：PASS 0   FAIL 0",
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
            Margin = new Padding(0, 0, 10, 0),
        };
        _driverBtn = new Button { Text = "驱动安装…", AutoSize = true, Anchor = AnchorStyles.Right, Margin = Padding.Empty, UseVisualStyleBackColor = true };
        _driverBtn.Click += (_, _) => RunDriverInstaller();
        phaseStrip.Controls.Add(_phaseLabel, 0, 0);
        phaseStrip.Controls.Add(_countLabel, 1, 0);
        phaseStrip.Controls.Add(_driverBtn, 2, 0);

        // ---- 串口列表（一行=一个 CH340 口，勾选要烧的口）----
        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            CheckBoxes = true,
            FullRowSelect = true,
            HideSelection = false,
            UseCompatibleStateImageBehavior = false,
            Font = new Font("Microsoft YaHei UI", 10.5f),
        };
        _list.Columns.Add("串口", 120).Name = "port";
        _list.Columns.Add("阶段", 260).Name = "phase";
        _list.Columns.Add("结果", 560).Name = "result";

        // ---- 日志区 ----
        var logPanel = new Panel { Dock = DockStyle.Bottom, Height = 168, Padding = new Padding(10, 4, 10, 8) };
        _logBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.White,
            Font = new Font("Consolas", 9f),
        };
        logPanel.Controls.Add(_logBox);

        // Dock 装配顺序：Fill 最先加入，随后 Bottom/Top（后加者先布局）
        Controls.Add(_list);
        Controls.Add(logPanel);
        Controls.Add(phaseStrip);
        Controls.Add(top);

        // 结果列自适应剩余宽（窄窗不挤出横滚、宽窗不留空）
        Resize += (_, _) => AutofitResultColumn();
        _list.HandleCreated += (_, _) => AutofitResultColumn();

        FormClosing += (_, e) =>
        {
            if (_busy && MessageBox.Show(this, "烧录正在进行，确定退出？", "确认",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.No)
                e.Cancel = true;
            FlushLog();
        };

        Load += (_, _) => { RefreshFirmwares(); RefreshPorts(); };
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
        foreach (var f in items)
            _fwCombo.Items.Add(new ComboItem(f.Path,
                $"{PenFlashCore.DescribeFirmware(f.FileName)}  —  {f.FileName}"));

        if (_fwCombo.Items.Count > 0)
        {
            // 下拉列表宽随最长项（固件名全露出——指挥官 10-22 五轮目验：名字截断不可接受）
            var widest = _fwCombo.Items.Cast<ComboItem>()
                .Max(i => TextRenderer.MeasureText(i.Display, _fwCombo.Font).Width);
            _fwCombo.DropDownWidth = Math.Max(_fwCombo.Width, widest + 24);
        }
        if (_fwCombo.Items.Count == 0)
        {
            UpdateInfo("固件目录没有 *.bin——把 bin 放进 材料\\固件\\ 后点【刷新固件】", Color.Firebrick);
            LogLine("[WARN] 固件目录没有 *.bin");
            return;
        }
        if (defaultPath is not null)
        {
            var idx = items.FindIndex(f => f.Path == defaultPath);
            if (idx >= 0) _fwCombo.SelectedIndex = idx;
            UpdateInfo($"出货版 {_shippingVersion}（已默认选中）；材料 {_engine.MaterialsRoot}", Color.DarkGreen);
        }
        else
        {
            _fwCombo.SelectedIndex = 0;
            UpdateInfo(_shippingVersion is null
                ? "出货版口径未配置（固件\\一期出货使用的*.txt 为空/缺失）——请手动选固件"
                : $"出货版 {_shippingVersion} 未在列表唯一匹配——请手动选固件",
                _shippingVersion is null ? Color.DimGray : Color.Firebrick);
        }
        LogLine($"固件扫描：{items.Count} 个 bin；出货版口径 = {_shippingVersion ?? "（未配置）"}");
    }

    private void RefreshPorts()
    {
        var checkedNow = _list.Items.Cast<ListViewItem>()
            .Where(i => i.Checked).Select(i => (string)i.Tag!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _list.Items.Clear();
        List<string> ports;
        try { ports = _engine.DetectCh340Ports(); }
        catch (Exception ex) { LogLine($"[ERR] 串口探测失败：{ex.Message}"); return; }

        foreach (var p in ports)
        {
            var item = new ListViewItem(p) { Tag = p };
            item.SubItems.Add("待烧").Name = "phase";
            item.SubItems.Add("").Name = "result";
            item.Checked = checkedNow.Contains(p);
            _list.Items.Add(item);
        }
        // 唯一候选才自动勾选；多候选不猜（01 号 §2.3-5 / 验收 4）
        if (checkedNow.Count == 0 && PenFlashCore.PickDefaultPort(ports) is { } auto)
            _list.Items[ports.IndexOf(auto)].Checked = true;
        if (ports.Count == 0)
        {
            _phaseLabel.Text = "未发现 CH340 治具（VID:PID=1A86:7523）——插好治具或安装驱动后点【刷新串口】";
            _phaseLabel.ForeColor = Color.DimGray;
            LogLine("[WARN] 未发现 CH340 治具（VID:PID=1A86:7523）——插好治具或安装驱动");
        }
        else
        {
            LogLine($"串口扫描：发现 {ports.Count} 个 CH340 口（{string.Join(", ", ports)}）");
        }
    }

    // ================= 烧录流程 =================

    private async void OnStartClick()
    {
        if (_busy)
            return;

        var firmware = (_fwCombo.SelectedItem as ComboItem)?.Path;
        var ports = _list.Items.Cast<ListViewItem>()
            .Where(i => i.Checked).Select(i => (string)i.Tag!).ToList();
        if (firmware is null) { MessageBox.Show(this, "请先选固件。", "提示"); return; }
        if (ports.Count == 0) { MessageBox.Show(this, "请先勾选要烧录的串口（多个候选必须明确勾选）。", "提示"); return; }

        _busy = true;
        _cts = new CancellationTokenSource();
        SetBusy(true);
        _phaseLabel.Text = "烧录中…";
        _phaseLabel.ForeColor = Color.FromArgb(160, 80, 0);
        foreach (var p in ports) SetRow(p, phase: "排队中", result: "");

        var fwName = Path.GetFileName(firmware);
        LogLine($"===== 开始：{fwName} → {string.Join(", ", ports)}（并发·每口独立 OmBleIsp 实例｜治具开关档位={Position}，请核对治具）=====");

        var sessions = ports.Select(p => new PortFlashSession(_engine, p, firmware, Position)).ToList();
        var results = new Dictionary<string, PortFlashSession>();
        try
        {
            foreach (var s in sessions)
            {
                s.Log += (_, line) => Ui(() => LogLine(line));
                s.PhaseChanged += (_, _) => Ui(() => SetRow(s.Port, phase: PhaseText(s.Phase)));
            }

            // 并发全开放（指挥官 10-22 令，撤实验位；多实例已实证：无互斥/工具目录零落文件/
            // 每口独立串口句柄+每口唯一名固件副本 fw_COMxx.bin——真机双 CH340 联测=实战自证）
            await Task.WhenAll(sessions.Select(s => s.RunAsync(_cts.Token))).ConfigureAwait(true);

            foreach (var s in sessions)
                results[s.Port] = s;

            // 每口终态回填行（结果列着色；会话内事件只刷阶段，终态统一在此收）
            foreach (var s in results.Values)
                SetRow(s.Port, phase: "结束", result: s.Summary, fail: s.Passed == false);
        }
        catch (OperationCanceledException) { /* 会话各自收 FAIL */ }

        var pass = results.Values.Count(s => s.Passed == true);
        var fail = results.Values.Count(s => s.Passed == false);
        _passCount += pass;
        _failCount += fail;
        _lastBatchAllPass = fail == 0 && results.Count > 0;
        _countLabel.Text = $"本班：PASS {_passCount}   FAIL {_failCount}";

        var verdict = results.Count == 1
            ? results.Values.Single().Passed == true ? "PASS" : "FAIL"
            : fail == 0 ? $"PASS（{pass} 支全过）" : $"FAIL {fail} / {results.Count}";
        var verdictColor = fail == 0 && results.Count > 0 ? Color.FromArgb(0, 140, 0) : Color.FromArgb(200, 0, 0);
        _phaseLabel.Text = verdict;
        _phaseLabel.ForeColor = verdictColor;

        LogLine($"===== 本轮结束：PASS {pass}，FAIL {fail} =====");

        // 抽检（可选，逐口；口已空闲）：1M 波特率读开机横幅比对所选固件版本
        if (_probeCheck.Checked && pass > 0)
        {
            await RunProbeAsync(ports, firmware).ConfigureAwait(true);
            _phaseLabel.Text = verdict;        // 抽检进度借阶段格显示，收尾还原本轮判定
            _phaseLabel.ForeColor = verdictColor;
        }

        _busy = false;
        SetBusy(false);
        _cts.Dispose();
        _cts = null;
        // 连续烧录（01 号 P0-5）：PASS 后换下一支笔直接再点；不做倒计时自动开烧（防笔没放好）
        _startBtn.Text = _lastBatchAllPass ? "继续下一支（勾选口）" : "重试（勾选口）";
        _startBtn.BackColor = _lastBatchAllPass ? Color.FromArgb(0, 120, 60) : Color.FromArgb(160, 80, 0);
    }

    private async Task RunProbeAsync(List<string> ports, string firmware)
    {
        var expected = PenFlashCore.ParseFirmwareVersion(Path.GetFileName(firmware));
        foreach (var p in ports)
        {
            _phaseLabel.Text = $"抽检 {p}：监听开机日志 8 秒（@{PenFlashCore.MonitorBaudrate}）…";
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

    /// <summary>
    /// 驱动安装（指挥官 10-22 目验：点一下直接把安装器弹出来，别只开文件夹）：
    /// 包内已预解压（驱动\x64\），定位安装器优先级 SETUP64/SETUP → 任意 exe；
    /// 都没有才回退打开文件夹（只剩 rar 未解压的裸包形态）。
    /// </summary>
    private void RunDriverInstaller()
    {
        var driverDir = Path.Combine(_engine.ToolDir, "驱动");
        if (!Directory.Exists(driverDir))
        {
            MessageBox.Show(this, $"找不到驱动目录：{driverDir}", "PenFlash");
            return;
        }

        var installer = Directory.EnumerateFiles(driverDir, "*.exe", SearchOption.AllDirectories)
            .OrderBy(p => Path.GetFileName(p).StartsWith("SETUP", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(p => Path.GetFileName(p).StartsWith("SETUP64", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault();
        if (installer is null)
        {
            // 裸包兜底：让操作员解压后手动装（ rar 在包内，人工可接受）
            System.Diagnostics.Process.Start("explorer.exe", $"\"{driverDir}\"");
            LogLine("[驱动] 未找到已解压的安装器——已打开驱动目录，请解压 rar 后双击安装");
            return;
        }
        LogLine($"[驱动] 调起安装器：{Path.GetFileName(installer)}（按引导装完再点【刷新串口】）");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = installer,
                WorkingDirectory = Path.GetDirectoryName(installer),
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            LogLine($"[驱动] 安装器调起失败：{ex.Message}——请手动到 驱动\\x64\\ 双击 {Path.GetFileName(installer)}");
        }
    }

    // ================= 杂项 =================

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _startBtn.Enabled = !busy;
        _refreshFwBtn.Enabled = !busy;
        _refreshPortBtn.Enabled = !busy;
        _fwCombo.Enabled = !busy;
        _probeCheck.Enabled = !busy;
        _armRadio.Enabled = !busy;
        _espRadio.Enabled = !busy;
        _cancelBtn.Enabled = busy;
    }

    private static string PhaseText(FlashPhase phase) => phase switch
    {
        FlashPhase.EnterIsp => "进 ISP",
        FlashPhase.Burn => "烧录+校验",
        FlashPhase.Run => "起跑",
        FlashPhase.CleanReset => "干净复位",
        FlashPhase.Done => "结束",
        _ => phase.ToString(),
    };

    /// <summary>结果列吃掉剩余宽度（List 列宽固定值之外的部分）。</summary>
    private void AutofitResultColumn()
    {
        if (_list.Columns.Count < 3) return;
        var others = _list.Columns[0].Width + _list.Columns[1].Width + 24;   // 24=表头边距冗余
        var w = _list.ClientSize.Width - others;
        _list.Columns[2].Width = w > 200 ? w : 200;
    }

    private void SetRow(string port, string? phase = null, string? result = null, bool? fail = null)
    {
        var item = _list.Items.Cast<ListViewItem>().FirstOrDefault(i => (string?)i.Tag == port);
        if (item is null) return;
        if (phase is not null)
            item.SubItems["phase"]!.Text = phase;
        if (result is not null)
        {
            item.SubItems["result"]!.Text = result;
            item.SubItems["result"]!.ForeColor = fail == true ? Color.FromArgb(200, 0, 0) : Color.FromArgb(0, 140, 0);
        }
    }

    /// <summary>会话终态回填行（PhaseChanged 后由 RunAsync 内事件触发不了终态色，这里统一挂）。</summary>
    private void UpdateInfo(string text, Color color)
    {
        _infoLabel.Text = text;
        _infoLabel.ForeColor = color;
    }

    private void Ui(Action action)
    {
        if (IsHandleCreated && !IsDisposed)
            BeginInvoke(action);
    }

    private void LogLine(string line)
    {
        var stamped = $"{DateTime.Now:HH:mm:ss} {line}";
        _logLines.Add(stamped);
        _logBox.AppendText(stamped + Environment.NewLine);
    }

    private void FlushLog()
    {
        if (_logLines.Count == 0)
            return;
        try
        {
            var logDir = Path.Combine(AppContext.BaseDirectory, "log");
            Directory.CreateDirectory(logDir);
            var path = Path.Combine(logDir, $"penflash-log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllLines(path, _logLines, new UTF8Encoding(true));
            _logBox.AppendText($"—— 日志已保存：{path}{Environment.NewLine}");
            _logLines.Clear();
        }
        catch
        {
            // 日志写盘失败不拦主流程（界面里已有全文）
        }
    }

    private sealed record ComboItem(string Path, string Display)
    {
        public override string ToString() => Display;
    }
}
