// <copyright file="CompanyParamCommands.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Text;
using System.Text.Json;
using PencilBox.Cli.Output;
using PencilBox.Core.Ble;
using PencilBox.Core.Communication;
using PencilBox.Core.Configuration;
using PencilBox.Core.Fsm;
using PencilBox.Core.Protocol;
using PencilBox.Core.Protocol.Company;
using PencilBox.Core.Services;
using PencilBox.Infrastructure.Serial;

namespace PencilBox.Cli.Commands;

/// <summary>
/// `company param` —— `0x61` 内部调参命令面（issues-pencil/01/02/03；**仅串口轨**笔端直连）。
///
/// CLI 直调 Codec + <see cref="CommandRequester"/>（同 <see cref="CompanyCommands"/> 模子，
/// TX/RX hex 原样露出便于黄金向量对拍）；Desktop `空鼠` 工作台走 <see cref="ITuningService"/> +
/// <see cref="IAirMouseSnapshotStore"/>（03 卷：UI 与 CLI 完全同源，接口零新增）。
/// 裁决方=笔端：越界/未知 ID 一律照发、按应答 `STATUS` 归因（02 卷 §2 行为表），
/// 内置范围表（<see cref="TuningParamTable"/>，含步长/宏名/文案）只用于**提示**与展示。
///
/// 命令一览：`list` 全表读 / `get` 单参读 / `set` 单参写 / `reset` 恢复出厂（03 §6）/
/// `export` 导出宏片段（03 §6 核心交付）/ `snapshot save|list|show|use|delete` 方案快照 A/B（03 §6）。
/// </summary>
public class CompanyParamCommands
{
    private readonly ISerialTransport _serialTransport;
    private readonly SerialCommandChannel _serialChannel;
    private readonly ICompanyCodec _codec;
    private readonly IAirMouseSnapshotStore _snapshots;
    private readonly IClock _clock;
    private readonly CompanyConfig _config;

    public CompanyParamCommands(
        ISerialTransport serialTransport,
        SerialCommandChannel serialChannel,
        ICompanyCodec codec,
        IAirMouseSnapshotStore snapshots,
        IClock clock,
        CompanyConfig config)
    {
        _serialTransport = serialTransport;
        _serialChannel = serialChannel;
        _codec = codec;
        _snapshots = snapshots;
        _clock = clock;
        _config = config;
    }

    // ============ list：全表读 ============

    /// <summary>`param list`：全表读（`PARAM_ID=0xFF`），打印 ID/名称/当前值/范围/单位；`--output json` 输出结构化数值</summary>
    public async Task<int> ListAsync(string? port, bool raw)
    {
        if (!await EnsureSerialAsync(port))
            return 1;

        var request = _codec.EncodeTuningRead(TuningParamMode.ParamIdAll);
        ConsoleFormatter.Info($"[PARAM] 0x61/01 全表读 ← {_serialChannel.Describe}");
        ConsoleFormatter.Info($"[PARAM] TX {request.Length}B: {Hex(request)}");

        var resp = await new CommandRequester(_clock, _config).RequestAsync(_serialChannel, request, CompanyCmd.TuningParam);
        if (resp == null)
            return NoAnswer(port);

        ConsoleFormatter.Info($"[PARAM] RX: {Hex(CompanyFrame.Build(CompanyFrame.BaseCmdResponse, CompanyCmd.TuningParam, resp))}");

        if (!_codec.TryParseTuningReadAllResp(resp, out var status, out var items))
        {
            ConsoleFormatter.Error("0x61 全表读应答结构非法（缺 STATUS/COUNT 或载荷不足 N×5）");
            return 1;
        }

        if (status != TuningStatus.Ok)
        {
            ConsoleFormatter.Error($"0x61 全表读失败：STATUS=0x{status:X2}");
            return 1;
        }

        var byId = items.ToDictionary(r => r.Id, r => r.Value);

        if (ConsoleFormatter.Format == OutputFormat.Json)
        {
            // 结构化数值输出（脚本/工作台取数口径；字符串仅展示位）
            var rows = TuningParamTable.All.Select(d => new
            {
                id = $"0x{d.Id:X2}",
                idNum = (int)d.Id,
                name = d.Name,
                value = byId.TryGetValue(d.Id, out var v) ? v : (float?)null,
                min = d.Min,
                max = d.Max,
                step = d.Step,
                factory = d.Factory,
                unit = d.Unit,
                macro = d.MacroName,
            }).ToList();
            Console.WriteLine(JsonSerializer.Serialize(rows, JsonIndented));
            return 0;
        }

        var tableRows = TuningParamTable.All.Select(d => new ParamRow(
            $"0x{d.Id:X2}",
            d.Name,
            byId.TryGetValue(d.Id, out var v2) ? TuningParamDescriptor.Format(v2) : "-",
            d.RangeText,
            d.Unit,
            raw && byId.TryGetValue(d.Id, out var v3) ? Value4Hex(v3) : ""));

        ConsoleFormatter.PrintTable(tableRows,
            ("ID", r => r.Id),
            ("名称", r => r.Name),
            ("当前值", r => r.Value),
            ("范围", r => r.Range),
            ("单位", r => r.Unit));

        // 前向兼容：笔端多出的未知 ID 原样露出（不静默吞）
        foreach (var extra in items.Where(r => TuningParamTable.Find(r.Id) == null))
            ConsoleFormatter.Warn($"笔端上报未知参数 ID=0x{extra.Id:X2}={TuningParamDescriptor.Format(extra.Value)}（工具表未收录，原样显示）");

        return 0;
    }

    // ============ get / set：单参读写 ============

    /// <summary>`param get`：单参读</summary>
    public async Task<int> GetAsync(string? port, byte paramId, bool raw)
    {
        if (!await EnsureSerialAsync(port))
            return 1;

        var request = _codec.EncodeTuningRead(paramId);
        ConsoleFormatter.Info($"[PARAM] 0x61/01 读参数 0x{paramId:X2} ← {_serialChannel.Describe}");
        ConsoleFormatter.Info($"[PARAM] TX {request.Length}B: {Hex(request)}");

        var resp = await new CommandRequester(_clock, _config).RequestAsync(_serialChannel, request, CompanyCmd.TuningParam);
        if (resp == null)
            return NoAnswer(port);

        ConsoleFormatter.Info($"[PARAM] RX: {Hex(CompanyFrame.Build(CompanyFrame.BaseCmdResponse, CompanyCmd.TuningParam, resp))}");

        if (!_codec.TryParseTuningReadResp(resp, out var status, out var echoId, out var value))
        {
            ConsoleFormatter.Error("0x61 单参读应答结构非法（缺 STATUS/PARAM_ID/VALUE4）");
            return 1;
        }

        if (status == TuningStatus.UnknownId)
        {
            ConsoleFormatter.Error($"未知参数 ID=0x{paramId:X2}（笔端不识别）。合法 ID：{TuningParamTable.IdListText}");
            return 1;
        }

        if (status != TuningStatus.Ok)
        {
            ConsoleFormatter.Error($"0x61 读参数失败：STATUS=0x{status:X2}");
            return 1;
        }

        PrintValue(echoId, value, raw);
        return 0;
    }

    /// <summary>
    /// `param set`：单参写；回显生效值与请求一致打 `[OK]`（02 卷 §2-1）。
    /// 越界/未知/OTA 会话中均按笔端应答 `STATUS` 归因（非零退出，不写入）。
    /// </summary>
    public async Task<int> SetAsync(string? port, byte paramId, float value, bool raw)
    {
        var (ok, _, _) = await WriteOneAsync(port, paramId, value, label: null);
        if (!ok)
            return 1;

        return 0;
    }

    // ============ reset：恢复出厂（03 卷 §6） ============

    /// <summary>
    /// `param reset`：把 6 参全部（或 `--only` 子集）写回出厂宏值（出厂值随 <see cref="TuningParamTable"/> 内置）。
    /// 逐参写→回显生效值必须等于出厂值；任一失败 exit 1。串行化：一参应答完再发下一参。
    /// </summary>
    public async Task<int> ResetAsync(string? port, IReadOnlyList<byte>? onlyIds, bool raw)
    {
        if (!await EnsureSerialAsync(port))
            return 1;

        var targets = onlyIds is { Count: > 0 }
            ? TuningParamTable.All.Where(d => onlyIds.Contains(d.Id)).ToList()
            : TuningParamTable.All.ToList();
        if (targets.Count == 0)
        {
            ConsoleFormatter.Error($"--only 未命中任何合法参数 ID。合法：{TuningParamTable.IdListText}");
            return 1;
        }

        ConsoleFormatter.Info($"[PARAM] 恢复出厂：{targets.Count} 项（{string.Join("、", targets.Select(t => t.Name))}）");
        int failed = 0;
        foreach (var d in targets)
        {
            var (ok, _, _) = await WriteOneAsync(port, d.Id, d.Factory, label: "恢复出厂", raw);
            if (!ok)
                failed++;
        }

        if (failed > 0)
        {
            ConsoleFormatter.Error($"恢复出厂完成但 {failed}/{targets.Count} 项失败（见上行归因；参数仍 RAM-only，重启亦回出厂）");
            return 1;
        }

        ConsoleFormatter.Success($"恢复出厂完成：{targets.Count}/{targets.Count} 项全部生效（掉电亦回出厂，此命令用于不重启即复位）");
        return 0;
    }

    // ============ export：导出宏片段（03 卷 §6 核心交付） ============

    /// <summary>
    /// `param export`：实读 6 参 + `0x03` 读笔固件版本 → 生成 `#define` 回填片段（
    /// <see cref="AirMouseMacroSnippet"/> 单点；`--out` 存文件，缺省打 stdout）。固件版本读不到不阻塞（标"未知"）。
    /// </summary>
    public async Task<int> ExportAsync(string? port, string? note, string? outFile)
    {
        if (!await EnsureSerialAsync(port))
            return 1;

        // 0x03 读版本（失败容忍：片段头部标"未知"，不阻塞导出）
        string firmware = "未知（0x03 无应答）";
        var verResp = await new CommandRequester(_clock, _config).RequestAsync(
            _serialChannel, _codec.EncodeReadVersion(), CompanyCmd.ReadVersion);
        if (verResp != null && _codec.TryParseVersionResp(verResp, out var ver) && ver.Status == CompanyStatus.Ok)
            firmware = $"MODEL=0x{ver.Model:X2}  SVER={ver.SverMajor}.{ver.SverMinor}.{ver.SverPatch}";

        // 0x61 全表读（导出数据源=笔端实读，不取本地缓存）
        var request = _codec.EncodeTuningRead(TuningParamMode.ParamIdAll);
        ConsoleFormatter.Info($"[PARAM] 导出：0x03 版本 + 0x61/01 全表读 ← {_serialChannel.Describe}");
        var resp = await new CommandRequester(_clock, _config).RequestAsync(_serialChannel, request, CompanyCmd.TuningParam);
        if (resp == null)
            return NoAnswer(port);

        if (!_codec.TryParseTuningReadAllResp(resp, out var status, out var items) || status != TuningStatus.Ok)
        {
            ConsoleFormatter.Error($"0x61 全表读失败（STATUS=0x{status:X2}），导出中止");
            return 1;
        }

        var snippet = AirMouseMacroSnippet.Build(items, firmware, note);

        if (string.IsNullOrWhiteSpace(outFile))
        {
            Console.Out.Write(snippet);
            ConsoleFormatter.Success($"（共 {items.Count} 项；--out <file> 可直接存文件）");
        }
        else
        {
            File.WriteAllText(outFile, snippet, new UTF8Encoding(false));
            ConsoleFormatter.Success($"宏片段已存：{outFile}（{items.Count} 项，固件 {firmware}）——交固件侧粘贴回填出包");
        }

        return 0;
    }

    // ============ snapshot：方案快照 A/B（03 卷 §6；仅本地 JSON，不下笔端） ============

    /// <summary>`param snapshot save &lt;名&gt;`：实读全表存快照（同名覆盖；`--note` 备注）</summary>
    public async Task<int> SnapshotSaveAsync(string? port, string name, string? note)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            ConsoleFormatter.Error("快照名不能为空：company param snapshot save <名> [--note \"...\"]");
            return 1;
        }

        var readings = await ReadAllOrThrowAsync(port);
        string firmware = "未知";
        var verResp = await new CommandRequester(_clock, _config).RequestAsync(
            _serialChannel, _codec.EncodeReadVersion(), CompanyCmd.ReadVersion);
        if (verResp != null && _codec.TryParseVersionResp(verResp, out var ver) && ver.Status == CompanyStatus.Ok)
            firmware = $"0.{ver.SverMajor}.{ver.SverMinor}.{ver.SverPatch}";

        _snapshots.Save(new AirMouseSnapshot(name.Trim(), DateTime.Now, note, firmware, readings));
        ConsoleFormatter.Success($"快照已存：{name.Trim()}（{readings.Count} 项，固件 {firmware}）——`snapshot use {name.Trim()}` 一键回切");
        return 0;
    }

    /// <summary>`param snapshot list`：快照清单</summary>
    public Task<int> SnapshotListAsync()
    {
        var names = _snapshots.ListNames();
        var rows = names.Select(n =>
        {
            var s = _snapshots.Load(n);
            return new
            {
                名称 = n,
                保存时间 = s?.SavedAt.ToString("yyyy-MM-dd HH:mm:ss") ?? "-",
                固件 = s?.Firmware ?? "-",
                备注 = s?.Note ?? "",
            };
        }).ToList();

        if (rows.Count == 0)
        {
            ConsoleFormatter.Info("（暂无快照——company param snapshot save <名> 创建；建议命名 A/B 便于互切对比）");
            return Task.FromResult(0);
        }

        ConsoleFormatter.PrintTable(rows,
            ("名称", r => r.名称),
            ("保存时间", r => r.保存时间),
            ("固件", r => r.固件),
            ("备注", r => r.备注));
        return Task.FromResult(0);
    }

    /// <summary>`param snapshot show &lt;名&gt;`：快照内容（含与出厂偏离）</summary>
    public Task<int> SnapshotShowAsync(string name)
    {
        var s = _snapshots.Load(name);
        if (s == null)
        {
            ConsoleFormatter.Error($"快照不存在：{name}（snapshot list 可列全部）");
            return Task.FromResult(1);
        }

        ConsoleFormatter.Info($"快照 {s.Name}（存于 {s.SavedAt:yyyy-MM-dd HH:mm:ss}，固件 {s.Firmware ?? "未知"}）" +
                              (string.IsNullOrWhiteSpace(s.Note) ? "" : $"  备注：{s.Note}"));
        var byId = s.Values.ToDictionary(r => r.Id, r => r.Value);
        var rows = TuningParamTable.All.Select(d => new
        {
            ID = $"0x{d.Id:X2}",
            名称 = d.Name,
            快照值 = byId.TryGetValue(d.Id, out var v) ? TuningParamDescriptor.Format(v) : "-",
            出厂值 = TuningParamDescriptor.Format(d.Factory),
            偏离 = byId.TryGetValue(d.Id, out var v2) && Math.Abs(v2 - d.Factory) > 1e-6f ? "★" : "",
        }).ToList();

        ConsoleFormatter.PrintTable(rows,
            ("ID", r => r.ID),
            ("名称", r => r.名称),
            ("快照值", r => r.快照值),
            ("出厂值", r => r.出厂值),
            ("偏离", r => r.偏离));
        return Task.FromResult(0);
    }

    /// <summary>
    /// `param snapshot use &lt;名&gt;`：把快照值依次写回笔端（A/B 互切的核心动作；逐参回显校验同 reset）。
    /// </summary>
    public async Task<int> SnapshotUseAsync(string? port, string name, bool raw)
    {
        var s = _snapshots.Load(name);
        if (s == null)
        {
            ConsoleFormatter.Error($"快照不存在：{name}（snapshot list 可列全部）");
            return 1;
        }

        if (!await EnsureSerialAsync(port))
            return 1;

        ConsoleFormatter.Info($"[PARAM] 应用快照 {s.Name}（{s.Values.Count} 项）");
        int failed = 0;
        foreach (var r in s.Values)
        {
            var d = TuningParamTable.Find(r.Id);
            if (d == null)
            {
                ConsoleFormatter.Warn($"快照含未知 ID=0x{r.Id:X2}，跳过（前向兼容）");
                continue;
            }

            var (ok, _, _) = await WriteOneAsync(port, r.Id, r.Value, label: $"快照 {s.Name}", raw);
            if (!ok)
                failed++;
        }

        if (failed > 0)
        {
            ConsoleFormatter.Error($"应用快照完成但 {failed} 项失败（见上行归因）");
            return 1;
        }

        ConsoleFormatter.Success($"快照 {s.Name} 已全部应用——直接动笔对比手感");
        return 0;
    }

    /// <summary>`param snapshot delete &lt;名&gt;`：删本地快照（笔端不受影响）</summary>
    public Task<int> SnapshotDeleteAsync(string name)
    {
        if (_snapshots.Load(name) == null)
        {
            ConsoleFormatter.Error($"快照不存在：{name}");
            return Task.FromResult(1);
        }

        _snapshots.Delete(name);
        ConsoleFormatter.Success($"快照已删：{name}（仅本地记录，笔端不受影响）");
        return Task.FromResult(0);
    }

    // ============ 私有：公共写路径 ============

    /// <summary>写一参并校验回显（set/reset/snapshot use 三入口共用）；失败已按 STATUS 归因打印。</summary>
    private async Task<(bool Ok, byte Status, float Effective)> WriteOneAsync(
        string? port, byte paramId, float value, string? label, bool raw = false)
    {
        if (!await EnsureSerialAsync(port))
            return (false, 0xFF, 0);

        var desc = TuningParamTable.Find(paramId);
        var prefix = label != null ? $"{label}：" : "";
        var request = _codec.EncodeTuningWrite(paramId, value);

        ConsoleFormatter.Info($"[PARAM] 0x61/02 写参数 0x{paramId:X2}={TuningParamDescriptor.Format(value)}" +
                              (desc != null ? $"（{desc.Name}，范围 {desc.RangeText}）" : "") + $" ← {_serialChannel.Describe}");
        ConsoleFormatter.Info($"[PARAM] TX {request.Length}B: {Hex(request)}");

        var resp = await new CommandRequester(_clock, _config).RequestAsync(_serialChannel, request, CompanyCmd.TuningParam);
        if (resp == null)
        {
            NoAnswer(port);
            return (false, 0xFF, 0);
        }

        ConsoleFormatter.Info($"[PARAM] RX: {Hex(CompanyFrame.Build(CompanyFrame.BaseCmdResponse, CompanyCmd.TuningParam, resp))}");

        if (!_codec.TryParseTuningWriteResp(resp, out var status, out var echoId, out var effective))
        {
            ConsoleFormatter.Error("0x61 写应答结构非法（缺 STATUS/PARAM_ID/VALUE4）");
            return (false, 0xFF, 0);
        }

        if (status == TuningStatus.UnknownId)
        {
            ConsoleFormatter.Error($"{prefix}未知参数 ID=0x{paramId:X2}（笔端不识别，未写入）。合法 ID：{TuningParamTable.IdListText}");
            return (false, status, effective);
        }

        if (status == TuningStatus.OutOfRange)
        {
            var range = desc != null ? desc.RangeText : "（工具表未收录该 ID，见 01 卷 §5）";
            ConsoleFormatter.Error(
                $"{prefix}越界拒绝：0x{paramId:X2} 允许范围 {range}（笔端不夹取，原值 {TuningParamDescriptor.Format(effective)} 未动）");
            return (false, status, effective);
        }

        if (status == TuningStatus.OtaBusy)
        {
            ConsoleFormatter.Error($"{prefix}OTA 升级会话进行中，调参写被拒：请先退出升级再调参（读不受影响）");
            return (false, status, effective);
        }

        if (status != TuningStatus.Ok)
        {
            ConsoleFormatter.Error($"{prefix}0x61 写参数失败：STATUS=0x{status:X2}");
            return (false, status, effective);
        }

        bool match = Math.Abs(effective - value) <= 1e-6f * Math.Max(1f, Math.Abs(value));
        if (!match)
        {
            ConsoleFormatter.Error($"{prefix}生效值 {TuningParamDescriptor.Format(effective)} ≠ 请求值 {TuningParamDescriptor.Format(value)}（被拒，未按请求生效）");
            return (false, status, effective);
        }

        ConsoleFormatter.Success($"{prefix}0x{echoId:X2} {desc?.Name ?? "未知 ID"} 生效={TuningParamDescriptor.Format(effective)}" +
                                 (desc != null ? $" {desc.Unit}" : "") +
                                 (label == null ? "——立即生效，掉电即失；满意后回填宏出包固化" : ""));

        if (raw)
            ConsoleFormatter.Info($"    VALUE4: {Value4Hex(effective)}");

        if (ConsoleFormatter.Format == OutputFormat.Json)
            Console.WriteLine(JsonSerializer.Serialize(new
                { ok = true, id = $"0x{echoId:X2}", effective },
                JsonIndented));
        return (true, status, effective);
    }

    private async Task<IReadOnlyList<TuningParamReading>> ReadAllOrThrowAsync(string? port)
    {
        if (!await EnsureSerialAsync(port))
            throw new InvalidOperationException("串口未就绪");

        var request = _codec.EncodeTuningRead(TuningParamMode.ParamIdAll);
        var resp = await new CommandRequester(_clock, _config).RequestAsync(_serialChannel, request, CompanyCmd.TuningParam)
            ?? throw new InvalidOperationException($"0x61 全表读无应答（{port} 超时）：确认笔端在位且固件含 0x61");

        if (!_codec.TryParseTuningReadAllResp(resp, out var status, out var items) || status != TuningStatus.Ok)
            throw new InvalidOperationException($"0x61 全表读失败：STATUS=0x{status:X2}");

        return items;
    }

    // ============ 私有：通用 ============

    /// <summary>串口开启（同 CompanyCommands.EnsureSerialAsync 口径：占用/离端口径一致）</summary>
    private async Task<bool> EnsureSerialAsync(string? port)
    {
        if (_serialChannel.IsOpen)
            return true;

        if (string.IsNullOrWhiteSpace(port))
        {
            ConsoleFormatter.Error("调参仅串口轨（PC↔笔端 UART1 直连，基站零感知）：请用 --port COMx 指定笔端串口");
            var ports = await _serialTransport.EnumeratePortsAsync();
            if (ports.Count > 0)
                ConsoleFormatter.Info("可用串口：" + string.Join(", ", ports.Select(p => p.PortName)));
            return false;
        }

        var settings = new SerialPortSettings { BaudRate = _config.SerialBaudRate };
        if (!await _serialTransport.ConnectAsync(port, settings))
        {
            ConsoleFormatter.Error($"打开串口失败：{port}（占用串口的程序先关——PencilBox.Desktop / sscom 教训；serial list 可列端口）");
            return false;
        }

        ConsoleFormatter.Info($"笔端命令通道就绪：{_serialChannel.Describe}");
        return true;
    }

    private static int NoAnswer(string? port)
    {
        ConsoleFormatter.Error(
            $"0x61 无应答（{port ?? "(未指定端口)"} 超时 500ms×4 次不悬挂）：serial probe 试读确认笔端在位；" +
            "占用串口的程序先关；确认笔端固件已含 0x61 支持（01 卷）");
        return 1;
    }

    private static void PrintValue(byte paramId, float value, bool raw)
    {
        var desc = TuningParamTable.Find(paramId);
        string name = desc != null ? desc.Name : "（未知 ID，原样显示）";
        string range = desc != null ? desc.RangeText : "-";
        string line = $"0x{paramId:X2} {name} = {TuningParamDescriptor.Format(value)}" +
                      (desc != null ? $" {desc.Unit}（范围 {range}）" : "");
        if (raw)
            line += $"  VALUE4: {Value4Hex(value)}";
        ConsoleFormatter.Success(line);

        if (ConsoleFormatter.Format == OutputFormat.Json)
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                id = $"0x{paramId:X2}", idNum = (int)paramId, name, value,
                min = desc?.Min, max = desc?.Max, step = desc?.Step,
                factory = desc?.Factory, unit = desc?.Unit, macro = desc?.MacroName,
            }, JsonIndented));
    }

    private static readonly JsonSerializerOptions JsonIndented = new() { WriteIndented = true };

    /// <summary>VALUE4 hex（大端，黄金向量对拍用）</summary>
    private static string Value4Hex(float v)
    {
        var b = new byte[4];
        int bits = BitConverter.SingleToInt32Bits(v);
        b[0] = (byte)(bits >> 24);
        b[1] = (byte)(bits >> 16);
        b[2] = (byte)(bits >> 8);
        b[3] = (byte)bits;
        return Convert.ToHexString(b);
    }

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);

    /// <summary>`list` 表行（PrintTable 在 `--output json` 下自动序列化本行；JSON 主路径见 ListAsync）</summary>
    private sealed record ParamRow(string Id, string Name, string Value, string Range, string Unit, string Value4Hex);
}
