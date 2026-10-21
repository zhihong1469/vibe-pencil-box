// <copyright file="OtaToolEngine.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Diagnostics;
using PencilBox.Core.Communication;
using PencilBox.Core.Fsm;
using PencilBox.Core.Protocol;
using PencilBox.Core.Protocol.Company;
using PencilBox.Infrastructure.Hid;

namespace PencilBox.Core.OtaTool;

/// <summary>单支升级终态（02 号 §4-7 口径：成功/跳过/失败/中断/待确认）。</summary>
public enum OtaDeviceOutcome { Success, Skip, Fail, Abort, Unconfirmed }

/// <summary>每支一行的结果（02 号 §4-7：序列号/旧版本→新版本/结果/耗时）。</summary>
public sealed record OtaDeviceResult(
    string Serial,
    OtaDeviceOutcome Outcome,
    string? BeforeVersion,
    string Detail,
    TimeSpan Elapsed)
{
    public string OutcomeText => Outcome switch
    {
        OtaDeviceOutcome.Success => "成功",
        OtaDeviceOutcome.Skip => "跳过",
        OtaDeviceOutcome.Fail => "失败",
        OtaDeviceOutcome.Abort => "已中断",
        _ => "待确认",
    };

    public string SummaryLine
    {
        get
        {
            var sn = string.IsNullOrEmpty(Serial) ? "(无序列号)" : Serial;
            return $"[{OutcomeText}] SN {sn}：{Detail}（耗时 {Elapsed.TotalSeconds:F0}s）";
        }
    }
}

/// <summary>引擎运行期更新（CLI 打印、GUI 状态列/进度条共用的唯一出口）。</summary>
public abstract record OtaToolUpdate
{
    /// <summary>日志行（info/warn/error/ok）。</summary>
    public sealed record Log(string Level, string Message) : OtaToolUpdate;

    /// <summary>当前阶段（连接设备/读当前版本/传输固件/等设备复位回来/回读版本）。</summary>
    public sealed record Phase(string Name) : OtaToolUpdate;

    /// <summary>传输百分比（FSM Progress 直通）。</summary>
    public sealed record Percent(int Value, int BlocksDone, int TotalBlocks) : OtaToolUpdate;
}

/// <summary>固件包装载结果——非法即抛 <see cref="OtaFirmwareLoadException"/>（外发口径：无跳过校验入口）。</summary>
public sealed record OtaFirmwarePackage(
    string FileName,
    byte[] Image,
    string SverText,
    byte SverMajor,
    byte SverMinor,
    byte SverPatch,
    int Crc16,
    string? Warning);

public sealed class OtaFirmwareLoadException : Exception
{
    public OtaFirmwareLoadException(string message) : base(message) { }
}

/// <summary>
/// 外发小工具升级引擎（03 号 §5）：扫描/识别/单支升级的唯一实现，CLI 与 GUI 共用零漂移。
/// 会话层与主线同一 CompanyOtaFsm（R1/R2/终态窗/终态丢失判定零漂移）；升级后复位重枚举
/// 三层兜底（等原路径消失→等重现→对拍宽限重试 3×5s）= 2026-10-21 真机竞态修复的落地。
/// v1 顺序队列裁决：同一时刻只开一条命令通道，失败语义简单优先。
/// </summary>
public sealed class OtaToolEngine
{
    /// <summary>复位重枚举等待上限（实测可慢达 ~2 分钟，超 02 号 §6 的 1 分钟口径）。</summary>
    public const int RecheckTimeoutMs = 120_000;

    /// <summary>对拍宽限：5×10s。3×5s 在"旧实例存活 30s+"形态下不够（2026-10-21 双台实测第③形态）。</summary>
    private const int ProbeIntervalMs = 1_000;

    private readonly CompanyConfig _config = new();
    private readonly ICompanyCodec _codec = new CompanyCodec();
    private readonly IClock _clock = new SystemClock();
    private static readonly Microsoft.Extensions.Logging.ILogger<HidSharpTransport> TransportLog =
        Microsoft.Extensions.Logging.Abstractions.NullLogger<HidSharpTransport>.Instance;
    private static readonly Microsoft.Extensions.Logging.ILogger<HidCommandChannel> ChannelLog =
        Microsoft.Extensions.Logging.Abstractions.NullLogger<HidCommandChannel>.Instance;

    private HidSharpTransport? _transport;
    private HidCommandChannel? _channel;
    private readonly IProgress<OtaToolUpdate>? _progress;

    public OtaToolEngine(IProgress<OtaToolUpdate>? progress = null) => _progress = progress;

    private void Say(string level, string message) => _progress?.Report(new OtaToolUpdate.Log(level, message));
    private void Phase(string name) => _progress?.Report(new OtaToolUpdate.Phase(name));

    // ================= 扫描/识别 =================

    public async Task<List<HidDeviceInfo>> EnumerateCandidatesAsync()
    {
        _transport ??= new HidSharpTransport(TransportLog);
        var devices = await _transport.EnumerateDevicesAsync();
        return HidDeviceSelection.CommandChannelInstances(devices);
    }

    /// <summary>按设备体系归组的扫描（一行=一台设备，组内挂全部通道）。</summary>
    public async Task<List<HidDeviceGroup>> ScanGroupsAsync() =>
        HidDeviceSelection.DeviceGroups(await EnumerateCandidatesAsync());

    /// <summary>读单台版本（开通道 → 0x03 → 关通道）。</summary>
    public async Task<(VersionInfo? Info, string? Error)> ReadVersionAsync(
        HidDeviceInfo device, CancellationToken cancellationToken = default)
    {
        if (!await OpenChannelAsync(device))
            return (null, "连接 USB 命令通道失败");

        var (info, err) = await ReadVersionOpenAsync(cancellationToken);
        CloseChannel();
        return (info, err);
    }

    // ================= 固件包装载 =================

    /// <summary>包校验 + 装载：R1P_（键盘/笔端）硬拒、尺寸上限、目标版本 = --sver 显式 &gt; 包名解析。</summary>
    public OtaFirmwarePackage LoadFirmware(string path, string? sverOverride = null)
    {
        var name = Path.GetFileName(path);
        var prefix = name.Length >= 4 && name[3] == '_' ? name[..3].ToUpperInvariant() : null;
        if (prefix == "R1P")
            throw new OtaFirmwareLoadException($"包 {name} 为键盘（笔端）固件（R1P_ 前缀），本工具只升基站——请使用 R1D_ 包");

        byte[] image;
        try
        {
            image = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            throw new OtaFirmwareLoadException($"固件包读取失败：{ex.Message}");
        }
        if (image.Length == 0)
            throw new OtaFirmwareLoadException("固件包为空");
        if (image.Length > _config.MaxImageBytes)
            throw new OtaFirmwareLoadException($"固件包 {image.Length}B 超设备上限 {_config.MaxImageBytes}B（0x38000）——设备侧也会拒收，已提前拦截");

        var sverText = sverOverride ?? ParseSverFromFileName(name);
        if (sverText == null || ParseSver(sverText) is not { } sver)
            throw new OtaFirmwareLoadException($"无法确定目标版本：包名解析失败（{name}）——命名规范 R1D_M.m.p_…，或显式指定版本");

        // 非 R1D 前缀（无法识别或 R1P 以外的杂前缀）一律提示，不拦截——防呆不防傻
        string? warning = prefix == "R1D"
            ? null
            : $"包名前缀异常（{name}）——本工具只升基站（R1D_），请确认这是基站固件包";

        return new OtaFirmwarePackage(name, image, sverText, sver.Item1, sver.Item2, sver.Item3,
            Crc16CcittFalse.Compute(image), warning);
    }

    // ================= 单支升级 =================

    /// <summary>单支升级全流程：连接 → 读版本（仅展示，同版本不拦截）→ OTA 会话 → 主动探测回读对拍。</summary>
    public async Task<OtaDeviceResult> UpgradeOneAsync(
        HidDeviceInfo device,
        OtaFirmwarePackage firmware,
        int abortAfterBlock = -1,
        int chunkGapMs = -1,
        bool skipRecheck = false,
        CancellationToken cancellationToken = default)
    {
        var sn = device.SerialNumber;
        var sverText = firmware.SverText;
        var sw = Stopwatch.StartNew();

        OtaDeviceResult Make(OtaDeviceOutcome outcome, string? before, string detail) =>
            new(sn, outcome, before, detail, sw.Elapsed);

        Phase("连接设备");
        if (!await OpenChannelAsync(device))
            return Make(OtaDeviceOutcome.Fail, null, "连接 USB 命令通道失败");

        Phase("读当前版本");
        var (before, err) = await ReadVersionOpenAsync(cancellationToken);
        if (before == null)
        {
            CloseChannel();
            return Make(OtaDeviceOutcome.Fail, null, $"读版本失败：{err}");
        }

        var beforeText = $"{before.SverMajor}.{before.SverMinor}.{before.SverPatch}";
        // 同版本不做软件层拦截（指挥官 10-21 裁决）：照常重刷，拦截与否由固件侧决定——
        // 固件真有拦截会话会自然报错，那才是真实口径；FSM 内 R2 同版本应答=协议语义不受此影响
        Say("info", $"SN={Show(sn)} 当前版本 {beforeText} → 目标 {sverText}，开始升级");

        // OTA 会话（与主线 CLI 同一 FSM：R1/R2/终态窗/终态丢失判定零漂移）
        Phase("传输固件");
        var fsm = new CompanyOtaFsm(_codec, _channel!, _clock, _config);
        fsm.Progress += (_, p) =>
        {
            var percent = p.DevicePercent >= 0 ? p.DevicePercent : p.Percent;
            _progress?.Report(new OtaToolUpdate.Percent(percent, p.BlocksDone, p.TotalBlocks));
        };
        fsm.Warning += (_, w) =>
        {
            // 重查/重发（R1/R2）为自动恢复路径 → WARN 不误报失败
            Say("warn", $"SN={Show(sn)} {w}");
        };

        OtaResult result;
        try
        {
            result = await fsm.RunAsync(firmware.Image, Downtype.BaseStation, _config.HverHid,
                firmware.SverMajor, firmware.SverMinor, firmware.SverPatch,
                abortAfterBlock, chunkGapMs, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            CloseChannel();
            return Make(OtaDeviceOutcome.Abort, beforeText, "用户取消");
        }
        catch (Exception ex)
        {
            CloseChannel();
            return Make(OtaDeviceOutcome.Fail, beforeText, $"升级异常：{ex.Message}");
        }

        Say("info", $"SN={Show(sn)} FSM 终态：{result.Outcome}（{result.Message}）");

        // 验证性中断（--abort-after-block）：设备留旧固件，不进入等回读流程
        if (abortAfterBlock >= 0)
        {
            CloseChannel();
            return Make(OtaDeviceOutcome.Abort, beforeText,
                $"验证性中断（{abortAfterBlock} 块后退出）：{result.Message}");
        }

        if (!result.IsTerminalSuccess)
        {
            CloseChannel();
            return Make(OtaDeviceOutcome.Fail, beforeText, result.Message);
        }

        // 等复位重枚举。两个坑都有实测（2026-10-21 真机）：
        // ① 终态先发再复位——旧枚举实例在复位完成前仍可连（读到旧 bank 版本，假失败形态），
        //    故先等原路径**消失**再等重现；② 重枚举实测可慢达 ~2 分钟（超 02 号 §6 的 1 分钟口径）。
        if (skipRecheck)
        {
            CloseChannel();
            return Make(OtaDeviceOutcome.Success, beforeText,
                $"升级会话完成（{result.Message}）；未复读版本（--no-recheck）");
        }

        CloseChannel();
        Phase("等设备复位回来（主动探测读版本）");
        // 复位重枚举通常一瞬间；Windows 枚举缓存却可让旧实例长存、句柄随时失效（10-21 真机两形态）——
        // "等路径消失→等重现"被动证据不可信，主动开句柄读版本对拍才是终态判据（指挥官 10-21 反馈）
        string? afterText = null;
        string? lastErr = null;
        var groupKey = HidDeviceSelection.DeviceGroupKey(device);
        var deadline = DateTime.UtcNow.AddMilliseconds(RecheckTimeoutMs);
        var probes = 0;
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            await SleepAsync(ProbeIntervalMs, cancellationToken);
            probes++;

            // 候选=原路径优先；重枚举换了路径按位置组键找回（组键含位置主体，同号双台勿串）
            var candidates = await EnumerateCandidatesAsync();
            var probe = candidates.FirstOrDefault(d =>
                    string.Equals(d.DevicePath, device.DevicePath, StringComparison.OrdinalIgnoreCase))
                ?? candidates.FirstOrDefault(d => HidDeviceSelection.DeviceGroupKey(d) == groupKey);
            if (probe == null)
            {
                lastErr = "枚举中暂无该设备";
                continue;
            }
            if (!await OpenChannelAsync(probe))
            {
                lastErr = "重连失败（复位中）";
                continue;
            }

            var (after, err2) = await ReadVersionOpenAsync(cancellationToken);
            CloseChannel();
            if (after != null)
            {
                afterText = $"{after.SverMajor}.{after.SverMinor}.{after.SverPatch}";
                if (afterText == sverText)
                {
                    Say("info", $"设备已回来（第 {probes} 次探测）");
                    Say("ok", $"SN={Show(sn)} 成功：{beforeText} → {afterText}");
                    return Make(OtaDeviceOutcome.Success, beforeText, $"成功：{beforeText} → {afterText}");
                }
                lastErr = $"回读 {afterText} ≠ 目标 {sverText}（旧实例仍在应答，继续等它刷新）";
            }
            else
            {
                lastErr = $"回读版本失败：{err2}";
            }
        }

        if (afterText != null)
        {
            // 会话终态成功 + 回读不符 ⇒ 无法证明失败（10-21 双台实测：慢复位台宽限期内仍答旧版本，
            // 稍后实读已到目标版本）——标"待确认"并给出复核动作，勿报失败
            return Make(OtaDeviceOutcome.Unconfirmed, beforeText,
                $"升级会话成功，但回读 {afterText} ≠ 目标 {sverText}（疑似复位未完成的旧实例）——请稍后重新识别复核");
        }

        return Make(OtaDeviceOutcome.Unconfirmed, beforeText,
            $"升级会话成功，{lastErr}——请重插后复核");
    }

    // ================= 通道/枚举/版本 =================

    private async Task<bool> OpenChannelAsync(HidDeviceInfo device)
    {
        _transport ??= new HidSharpTransport(TransportLog);
        _channel ??= new HidCommandChannel(_transport, ChannelLog);
        if (!await _transport.ConnectAsync(device.DevicePath))
            return false;
        return _channel.IsOpen;
    }

    private void CloseChannel() => _ = _transport?.DisconnectAsync();

    /// <summary>0x03 读基站版本（target=0x00 基站自答；500ms×≤3 重发不悬挂）。须先开通道。</summary>
    private async Task<(VersionInfo?, string?)> ReadVersionOpenAsync(CancellationToken cancellationToken)
    {
        if (_channel == null || !_channel.IsOpen)
            return (null, "通道未打开");

        var requester = new CommandRequester(_clock, _config);
        var request = _codec.EncodeReadVersion(0x00);
        var resp = await requester.RequestAsync(_channel, request, CompanyCmd.ReadVersion, cancellationToken: cancellationToken);
        if (resp == null)
            return (null, $"无应答（{_config.ResponseTimeoutMs}ms × {_config.MaxRetry + 1} 次）");

        if (!_codec.TryParseVersionResp(resp, out var info))
            return (null, "应答结构非法");
        if (info.Status != CompanyStatus.Ok)
            return (null, $"STATUS=0x{info.Status:X2}（设备未定义型号/版本）");

        return (info, null);
    }

    /// <summary>可取消的延时（取消不当失败——吞 OCE，调用方下一拍自查）。</summary>
    private static async Task SleepAsync(int ms, CancellationToken cancellationToken) =>
        await Task.Delay(ms, cancellationToken).ContinueWith(_ => { });

    // ================= 杂项 =================

    private static string Show(string s) => string.IsNullOrEmpty(s) ? "(无序列号)" : s;

    /// <summary>包名 → 版本（`R1D_1.0.2_rc.1_win_4f245b6.bin` → "1.0.2"；命名规范 `&lt;target&gt;_&lt;ext_ver&gt;_...`）。</summary>
    private static string? ParseSverFromFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        var parts = name.Split('_');
        if (parts.Length < 2)
            return null;
        return ParseSver(parts[1]) != null ? parts[1] : null;
    }

    /// <summary>"M.m.p" → 三段 byte；非法返回 null（net48 无 StringSplitOptions.TrimEntries——逐段 Trim）。</summary>
    public static (byte, byte, byte)? ParseSver(string text)
    {
        var parts = text.Split('.').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
        if (parts.Length != 3 || parts.Any(p => !byte.TryParse(p, out _)))
            return null;
        return (byte.Parse(parts[0]), byte.Parse(parts[1]), byte.Parse(parts[2]));
    }
}
