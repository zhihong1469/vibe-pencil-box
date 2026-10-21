// <copyright file="MainForm.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Communication;
using PencilBox.Core.OtaTool;
using System.Windows.Forms;

namespace PencilBox.OtaGui;

/// <summary>
/// 多支同升主界面（03 号 §5 定稿）：**顶层选固件**（固件一对多），**下方设备列表**
/// 按设备体系一台一行（勾选要升的台），逐支顺序升级（v1 裁决）。
/// 业务全在 OtaToolEngine——界面只做状态呈现与按钮，升级语义与 CLI 零漂移。
/// </summary>
internal sealed class MainForm : Form
{
    private readonly OtaToolEngine _engine;
    private readonly IProgress<OtaToolUpdate> _ui;   // Progress<T> 自动回 UI 线程

    private readonly TextBox _fwBox;
    private readonly Label _fwInfo;
    private readonly Label _phaseLabel;
    private readonly Button _browseBtn;
    private readonly Button _refreshBtn;
    private readonly Button _upgradeAllBtn;
    private readonly Button _upgradeCheckedBtn;
    private readonly Button _cancelBtn;
    private readonly ProgressBar _progressBar;
    private readonly ListView _list;
    private readonly TextBox _logBox;

    private OtaFirmwarePackage? _fw;
    private CancellationTokenSource? _cts;
    private ListViewItem? _current;   // 正在升级的行（进度/阶段写它的状态列）
    private readonly List<string> _logLines = new();
    private bool _busy;

    public MainForm()
    {
        Text = "多支同升 · USB 升级小工具（PencilBox 裁剪版）";
        Font = new System.Drawing.Font("Microsoft YaHei UI", 9f);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new System.Drawing.Size(860, 560);
        Size = new System.Drawing.Size(980, 680);

        _ui = new Progress<OtaToolUpdate>(OnUpdate);
        _engine = new OtaToolEngine(_ui);

        // ---- 顶层：固件选择（一对多的"一"）+ 动作按钮 ----
        var top = new Panel { Dock = DockStyle.Top, Height = 92, Padding = new Padding(10, 8, 10, 0) };
        var fwRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, WrapContents = false };
        fwRow.Controls.Add(new Label
        {
            Text = "固件包：",
            AutoSize = true,
            Margin = new Padding(3, 8, 0, 0),
        });
        _fwBox = new TextBox
        {
            Width = 560,
            // 可编辑：支持直接粘贴固件路径（TextChanged 即装载校验），不必点浏览
        };
        _fwBox.TextChanged += (_, _) => LoadFirmware(_fwBox.Text);
        fwRow.Controls.Add(_fwBox);
        _browseBtn = new Button { Text = "浏览…", AutoSize = true };
        _browseBtn.Click += (_, _) => PickFirmware();
        fwRow.Controls.Add(_browseBtn);
        _refreshBtn = new Button { Text = "刷新设备", AutoSize = true };
        _refreshBtn.Click += (_, _) => _ = RefreshDevicesAsync();
        fwRow.Controls.Add(_refreshBtn);

        var actionRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, WrapContents = false };
        _upgradeAllBtn = new Button { Text = "升级全部", AutoSize = true };
        _upgradeAllBtn.Click += (_, _) => _ = RunUpgradeAsync(_list.Items.Cast<ListViewItem>().ToList());
        _upgradeCheckedBtn = new Button { Text = "升级勾选", AutoSize = true };
        _upgradeCheckedBtn.Click += (_, _) => _ = RunUpgradeAsync(_list.Items.Cast<ListViewItem>().Where(i => i.Checked).ToList());
        _cancelBtn = new Button { Text = "取消", AutoSize = true, Enabled = false };
        _cancelBtn.Click += (_, _) =>
        {
            LogLine("已请求取消（FSM 会优雅退出：发 DCMD=00 后收敛）…");
            _cts?.Cancel();
        };
        actionRow.Controls.Add(_upgradeAllBtn);
        actionRow.Controls.Add(_upgradeCheckedBtn);
        actionRow.Controls.Add(_cancelBtn);
        _fwInfo = new Label
        {
            Text = "未加载固件——请选择 R1D_ 开头的基站固件包",
            AutoSize = true,
            Margin = new Padding(12, 10, 0, 0),
            ForeColor = System.Drawing.Color.DimGray,
        };
        actionRow.Controls.Add(_fwInfo);

        top.Controls.Add(actionRow);
        top.Controls.Add(fwRow);

        // ---- 进度条 + 阶段标签 ----
        _phaseLabel = new Label
        {
            Text = "就绪",
            Dock = DockStyle.Top,
            Height = 22,
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 2, 0, 0),
        };
        _progressBar = new ProgressBar { Dock = DockStyle.Top, Height = 14 };

        // ---- 设备列表（一行=一台设备体系）----
        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            CheckBoxes = true,
            FullRowSelect = true,
            HideSelection = false,
            UseCompatibleStateImageBehavior = false,
        };
        _list.Columns.Add("设备（序列号）", 200).Name = "sn";
        _list.Columns.Add("位置（端口片段）", 200).Name = "loc";
        _list.Columns.Add("通道数", 70).Name = "ch";
        _list.Columns.Add("当前版本", 100).Name = "ver";
        _list.Columns.Add("状态", 330).Name = "status";

        // ---- 日志区 ----
        var logPanel = new Panel { Dock = DockStyle.Bottom, Height = 170, Padding = new Padding(10, 4, 10, 8) };
        _logBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = System.Drawing.Color.White,
            Font = new System.Drawing.Font("Consolas", 9f),
        };
        logPanel.Controls.Add(_logBox);

        // Dock 装配顺序：Fill 最先加入，随后 Bottom/Top（后加者先布局）
        Controls.Add(_list);
        Controls.Add(logPanel);
        Controls.Add(_phaseLabel);
        Controls.Add(_progressBar);
        Controls.Add(top);

        FormClosing += (_, e) =>
        {
            if (_busy && MessageBox.Show(this, "升级正在进行，确定退出？", "确认",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.No)
                e.Cancel = true;
            FlushLog();
        };

        LogLine("就绪。步骤：① 选固件包 → ② 刷新设备（自动读每台当前版本）→ ③ 勾选/全部升级。");
        LogLine("同一固件可升多台；已是目标版本的台会自动跳过不重刷。");
    }

    // ================= 设备扫描 =================

    private async Task RefreshDevicesAsync()
    {
        SetBusy(true);
        _phaseLabel.Text = "扫描设备…";
        try
        {
            _list.Items.Clear();
            var groups = await _engine.ScanGroupsAsync();
            foreach (var g in groups)
                _list.Items.Add(MakeItem(g));
            LogLine($"扫描：在插可升级设备 {groups.Count} 台（按设备体系分组）");
            _phaseLabel.Text = groups.Count == 0
                ? "未发现可升级设备——确认接收器已插入且此电脑能识别"
                : $"发现 {groups.Count} 台设备";

            foreach (ListViewItem item in _list.Items)
            {
                var dev = ((HidDeviceGroup)item.Tag).CommandChannel;
                Cell(item, "ver").Text = "读取中…";
                var (info, err) = await _engine.ReadVersionAsync(dev);
                if (info == null)
                {
                    Cell(item, "ver").Text = "读取失败";
                    LogLine($"SN {Show(dev.SerialNumber)} 读版本失败：{err}");
                }
                else
                {
                    Cell(item, "ver").Text = $"{info.SverMajor}.{info.SverMinor}.{info.SverPatch}";
                    LogLine($"SN {Show(dev.SerialNumber)} 当前版本 {info.SverMajor}.{info.SverMinor}.{info.SverPatch}");
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"扫描失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static ListViewItem MakeItem(HidDeviceGroup g)
    {
        var anchor = g.CommandChannel;
        var item = new ListViewItem(Show(anchor.SerialNumber)) { Tag = g };
        item.SubItems[0].Name = "sn";
        item.SubItems.Add(anchor.LocationToken).Name = "loc";
        item.SubItems.Add(g.Channels.Count.ToString()).Name = "ch";
        item.SubItems.Add("—").Name = "ver";
        item.SubItems.Add("就绪").Name = "status";
        item.Checked = true;
        return item;
    }

    // ================= 固件选择 =================

    private void PickFirmware()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "选择基站固件包（R1D_ 开头）",
            Filter = "固件包 (*.bin)|*.bin|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _fwBox.Text = dlg.FileName;
    }

    private void LoadFirmware(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _fw = null;
            _fwInfo.ForeColor = System.Drawing.Color.DimGray;
            _fwInfo.Text = string.IsNullOrWhiteSpace(path)
                ? "未加载固件——请选择 R1D_ 开头的基站固件包"
                : "固件包不存在";
            return;
        }

        OtaFirmwarePackage? fw = null;
        try
        {
            fw = _engine.LoadFirmware(path);
        }
        catch (OtaFirmwareLoadException ex)
        {
            _fwInfo.Text = ex.Message;
            _fwInfo.ForeColor = System.Drawing.Color.Firebrick;
            LogLine("固件不可用：" + ex.Message);
            return;
        }

        _fw = fw;
        _fwInfo.ForeColor = System.Drawing.Color.DimGray;
        _fwInfo.Text = $"{fw.FileName} · {fw.Image.Length}B（{(fw.Image.Length + 1023) / 1024} 块）· CRC16 0x{fw.Crc16:X4} · 目标版本 {fw.SverText}";
        LogLine("已加载固件：" + _fwInfo.Text);
        if (fw.Warning != null)
        {
            LogLine("注意：" + fw.Warning);
            MessageBox.Show(this, fw.Warning, "包名前缀异常", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ================= 升级 =================

    private async Task RunUpgradeAsync(IReadOnlyList<ListViewItem> queue)
    {
        if (_busy)
            return;
        if (_fw == null)
        {
            MessageBox.Show(this, "请先选择固件包（R1D_ 开头的基站固件）。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (queue.Count == 0)
        {
            MessageBox.Show(this, "请先勾选要升级的设备（或点\"升级全部\"）。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _cts = new CancellationTokenSource();
        SetBusy(true);
        BeginLog();
        LogLine($"开始升级：{queue.Count} 台，固件 {_fw.FileName}（目标 {_fw.SverText}）——升级全程请勿拔下任何设备");

        var ok = 0;
        var bad = 0;
        try
        {
            foreach (var item in queue)
            {
                if (_cts.IsCancellationRequested)
                {
                    SetItemStatus(item, "已跳过（取消）");
                    continue;
                }

                _current = item;
                var g = (HidDeviceGroup)item.Tag;
                SetItemStatus(item, "连接设备…");
                _progressBar.Value = 0;
                LogLine($"—— {Show(g.CommandChannel.SerialNumber)}（位置 {g.CommandChannel.LocationToken}）——");

                var r = await _engine.UpgradeOneAsync(g.CommandChannel, _fw, cancellationToken: _cts.Token);
                SetItemStatus(item, $"{r.OutcomeText}：{r.Detail}");
                if (r.Outcome is OtaDeviceOutcome.Success)
                {
                    Cell(item, "ver").Text = _fw.SverText;   // 成功=对拍通过，即目标版本
                    ok++;
                }
                else if (r.Outcome is OtaDeviceOutcome.Skip)
                {
                    ok++;
                }
                else
                {
                    bad++;
                }
                _current = null;
            }

            var summary = $"升级结束：{ok} 台成功（含跳过），{bad} 台失败/待确认";
            LogLine(summary);
            MessageBox.Show(this,
                summary + (bad == 0 ? "\n\n请拔下设备插回一体机使用。" : "\n\n详情见界面日志。"),
                "升级完成", MessageBoxButtons.OK,
                bad == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            LogLine($"升级异常：{ex.Message}");
            MessageBox.Show(this, $"升级异常：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _current = null;
            FlushLog();
            SetBusy(false);
            _phaseLabel.Text = "就绪";
        }
    }

    // ================= 引擎更新（UI 线程） =================

    private void OnUpdate(OtaToolUpdate update)
    {
        switch (update)
        {
            case OtaToolUpdate.Log log:
                LogLine(log.Message);
                break;
            case OtaToolUpdate.Phase phase:
                _phaseLabel.Text = phase.Name;
                if (_current != null)
                    SetItemStatus(_current, phase.Name);
                break;
            case OtaToolUpdate.Percent pct:
                _progressBar.Value = Math.Min(100, Math.Max(0, pct.Value));
                if (_current != null)
                    SetItemStatus(_current, $"传输 {pct.Value}%（{pct.BlocksDone}/{pct.TotalBlocks} 块）");
                break;
        }
    }

    // ================= 杂项 =================

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _browseBtn.Enabled = !busy;
        _refreshBtn.Enabled = !busy;
        _upgradeAllBtn.Enabled = !busy;
        _upgradeCheckedBtn.Enabled = !busy;
        _cancelBtn.Enabled = busy;
    }

    private void SetItemStatus(ListViewItem item, string text) => Cell(item, "status").Text = text;

    private static ListViewItem.ListViewSubItem Cell(ListViewItem item, string key) => item.SubItems[key]!;

    private void LogLine(string line)
    {
        var stamped = $"{DateTime.Now:HH:mm:ss.fff} {line}";
        _logLines.Add(stamped);
        _logBox.AppendText(stamped + Environment.NewLine);
    }

    private void BeginLog()
    {
        _logLines.Clear();
        LogLine($"PencilBox 裁剪版升级小工具（GUI）  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        LogLine($"机器：{Environment.MachineName}");
    }

    private void FlushLog()
    {
        if (_logLines.Count == 0)
            return;
        try
        {
            var logDir = Path.Combine(AppContext.BaseDirectory, "log");
            Directory.CreateDirectory(logDir);
            var path = Path.Combine(logDir, $"ota-gui-log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllLines(path, _logLines, new System.Text.UTF8Encoding(true));
            _logBox.AppendText($"—— 日志已保存：{path}{Environment.NewLine}");
            _logLines.Clear();
        }
        catch
        {
            // 日志写盘失败不拦主流程（界面里已有全文）
        }
    }

    private static string Show(string s) => string.IsNullOrEmpty(s) ? "(无序列号)" : s;
}
