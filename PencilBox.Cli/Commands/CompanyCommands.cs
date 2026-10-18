// <copyright file="CompanyCommands.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Cli.Output;
using PencilBox.Core.Ble;
using PencilBox.Core.Communication;
using PencilBox.Core.Fsm;
using PencilBox.Core.Protocol;
using PencilBox.Core.Protocol.Company;
using PencilBox.Infrastructure.Hid;
using PencilBox.Infrastructure.Serial;

namespace PencilBox.Cli.Commands;

/// <summary>
/// 公司 V0.9 协议命令面（CLI 先行的端到端入口）。
///
/// 分层落点：Codec（<see cref="ICompanyCodec"/>，纯函数）/ Channel（<see cref="HidCommandChannel"/>、
/// <see cref="SerialCommandChannel"/>，双通道并存按会话路由）/ FSM（<see cref="CompanyPairFsm"/>）。
/// 依据：`agent/box/docs/issues/0-待处理/04-状态机协议-骨架与落地步骤.md` §6（S3 CLI 先行）。
/// </summary>
public class CompanyCommands
{
    private readonly IHidTransport _hidTransport;
    private readonly ISerialTransport _serialTransport;
    private readonly HidCommandChannel _usbChannel;
    private readonly SerialCommandChannel _serialChannel;
    private readonly ICompanyCodec _codec;
    private readonly IClock _clock;
    private readonly CompanyConfig _config;

    public CompanyCommands(
        IHidTransport hidTransport,
        ISerialTransport serialTransport,
        HidCommandChannel usbChannel,
        SerialCommandChannel serialChannel,
        ICompanyCodec codec,
        IClock clock,
        CompanyConfig config)
    {
        _hidTransport = hidTransport;
        _serialTransport = serialTransport;
        _usbChannel = usbChannel;
        _serialChannel = serialChannel;
        _codec = codec;
        _clock = clock;
        _config = config;
    }

    /// <summary>
    /// `company identify`：`0x03` 读型号/版本（默认 USB 基站；`--port` 走串口笔端）。
    /// G14 冻结（issues/05 §1-D）：串口轨 `target` 恒 `0x00`；USB 读**笔端**（`--target pen`，经基站 B1 透传）
    /// 须显式填笔端类别（<see cref="CompanyConfig.PenTargetClass"/>）；读基站 `0x00`。
    /// </summary>
    /// <param name="port">指定则走串口（笔端直连，target 恒 0x00）</param>
    /// <param name="target">`bs`（默认，读基站）| `pen`（读笔端；仅 USB 轨生效）</param>
    public async Task<int> IdentifyAsync(string? port, string? target = null)
    {
        var channel = await ResolveChannelAsync(port);
        if (channel == null)
            return 1;

        // G14：target 路由（串口轨恒 0x00 —— 黄金向量口径不变；无线轨问笔端须显式填类别）
        byte targetByte = 0x00;
        string targetDesc = port != null ? "笔端（串口轨，target 恒 0x00）" : "基站（target=0x00，基站自答）";
        if (target?.ToLowerInvariant() is "pen" or "pencil")
        {
            if (port != null)
                targetDesc = "笔端（串口轨，target 恒 0x00 —— --target 被忽略）";
            else
            {
                targetByte = _config.PenTargetClass;
                targetDesc = $"笔端（经基站 B1 透传，target=0x{targetByte:X2}；无应答 ≠ 版本一致）";
            }
        }

        var requester = new CommandRequester(_clock, _config);
        var request = _codec.EncodeReadVersion(targetByte);

        ConsoleFormatter.Info($"[COMPANY] 0x03 读型号/版本 ← {channel.Describe}（目标：{targetDesc}）");
        ConsoleFormatter.Info($"[COMPANY] TX {request.Length}B: {Hex(request)}");

        var resp = await requester.RequestAsync(channel, request, CompanyCmd.ReadVersion);
        if (resp == null)
        {
            ConsoleFormatter.Error($"0x03 无应答（{_config.ResponseTimeoutMs}ms × {_config.MaxRetry + 1} 次，不悬挂）");
            return 1;
        }

        ConsoleFormatter.Info($"[COMPANY] RX: {Hex(CompanyFrame.Build(CompanyFrame.BaseCmdResponse, CompanyCmd.ReadVersion, resp))}");

        if (!_codec.TryParseVersionResp(resp, out var info))
        {
            ConsoleFormatter.Error("0x03 应答结构非法（长度不足）");
            return 1;
        }

        if (info.Status != CompanyStatus.Ok)
        {
            ConsoleFormatter.Error($"STATUS=0x{info.Status:X2}：设备未定义型号/版本（原样显示，不冒充）");
            return 1;
        }

        // G14 防错闸（红线 12 配套）：无线轨显式填了笔端类别，但应答 MODEL 类别段不符 ⇒
        // 疑似**基站自答冒充**（基站固件未按 target 过滤/透传）——真机 2026-09-17 实测复现。
        if (targetByte != 0x00 && (info.Model & 0xF0) != (targetByte & 0xF0))
        {
            ConsoleFormatter.Error(
                $"应答类别不符：请求 target=0x{targetByte:X2}（笔端），应答 MODEL=0x{info.Model:X2}（基站自答）——" +
                "疑似基站固件尚未实现 G14 过滤（透传笔端代答）；本次读到的**不是笔端版本**，不可信");
            return 1;
        }

        ConsoleFormatter.Success($"MODEL=0x{info.Model:X2}（{ModelText(info.Model)}）" +
                                 $"  SVER={info.SverMajor}.{info.SverMinor}.{info.SverPatch}");
        return 0;
    }

    /// <summary>
    /// `company hash`：`0x04` 读 Git Hash 内部指令（定稿 2026-10-16，40 字符 hex ASCII）。
    /// 双轨各自直达：`--port` = 笔端插线串口（canonical `target=0x11`，0x61 同轨）；
    /// 无 `--port` = 基站 USB（`target=0x00`）。**无线轨不开放**——USB 轨问笔端（`--target pen`）
    /// 直接提示不支持（定稿 §3.3/§8-1：40B 载荷占 BLE 带宽与音频实时传输相冲，留扩展位另立变更）。
    /// 依赖固件 `INTERNAL_DEBUG_CMD_ENABLE` 构建：量产构建 `case 0x04` 整体裁除 → 无应答属预期（§3.7）。
    /// </summary>
    /// <param name="port">指定则走串口（笔端，target=0x11）；否则 USB（基站，target=0x00）</param>
    /// <param name="target">仅 USB 轨校验：`pen` = 无线读笔端 hash → 不支持；串口轨忽略（恒 0x11）</param>
    public async Task<int> HashAsync(string? port, string? target = null)
    {
        // 无线轨不开放（定案 §8-1）：USB 问笔端 = 经基站 BLE——明确提示不支持，不发帧空等
        if (port == null && target?.ToLowerInvariant() is "pen" or "pencil")
        {
            ConsoleFormatter.Error("0x04 读笔端 hash 仅支持插线串口直达（--port COMx，与 0x61 调参同轨）——" +
                                   "无线轨（经基站 BLE）不开放：40B 载荷占 BLE 带宽、与音频实时传输相冲" +
                                   "（定稿 §3.3/§8-1，将来放开须另立变更申请）");
            return 1;
        }

        var channel = await ResolveChannelAsync(port);
        if (channel == null)
            return 1;

        // canonical（定稿 §3.2）：笔端串口轨 0x11、基站 USB 轨 0x00（串口轨对任意 target 恒应答自身，按 canonical 填）
        byte targetByte = port != null ? (byte)0x11 : (byte)0x00;
        string targetDesc = port != null
            ? "笔端（插线串口，target=0x11）"
            : "基站（USB，target=0x00）";
        if (port != null && target != null)
            targetDesc += "（--target 被忽略：串口轨 canonical 0x11）";

        var requester = new CommandRequester(_clock, _config);
        var request = _codec.EncodeReadHash(targetByte);

        ConsoleFormatter.Info($"[COMPANY] 0x04 读 Git Hash ← {channel.Describe}（目标：{targetDesc}）");
        ConsoleFormatter.Info($"[COMPANY] TX {request.Length}B: {Hex(request)}");

        var resp = await requester.RequestAsync(channel, request, CompanyCmd.ReadHash);
        if (resp == null)
        {
            ConsoleFormatter.Error($"0x04 无应答（{_config.ResponseTimeoutMs}ms × {_config.MaxRetry + 1} 次）——" +
                                   "固件须 INTERNAL_DEBUG_CMD_ENABLE 构建（量产构建裁除 0x04，无应答属预期，定稿 §3.7）");
            return 1;
        }

        ConsoleFormatter.Info($"[COMPANY] RX: {Hex(CompanyFrame.Build(CompanyFrame.BaseCmdResponse, CompanyCmd.ReadHash, resp))}");

        if (!_codec.TryParseHashResp(resp, out var status, out var hash))
        {
            ConsoleFormatter.Error("0x04 应答结构非法（缺 STATUS 或 HASH 不足 40 字符）");
            return 1;
        }

        if (status != CompanyStatus.Ok)
        {
            ConsoleFormatter.Error($"STATUS=0x{status:X2}：hash 不可用（定稿 §3.5，理论不发生——固件编译期内嵌）");
            return 1;
        }

        ConsoleFormatter.Success($"GIT_HASH={hash}");
        if (hash.All(c => c == '0'))
            ConsoleFormatter.Warn("HASH 为占位全零——构建缺 git 信息（定稿 §3.4 宏缺席兜底），现场溯源改用 boot banner/归档 CSV");

        // 对拍锚（定稿 §2/§3.4）：包名第 5 字段 = 7 位 short hash；boot banner `* Commit:` 行同源
        ConsoleFormatter.Info($"对拍：包名 short hash（第 5 字段）应 = {hash[..7]}；boot banner `* Commit:` 行应与本值一致");
        return 0;
    }

    /// <summary>`company pair read`：`0x51` 读配对信息（`--port` 走串口笔端，否则 USB 基站）。</summary>
    public async Task<int> PairReadAsync(string? port, byte no)
    {
        var channel = await ResolveChannelAsync(port);
        if (channel == null)
            return 1;

        var requester = new CommandRequester(_clock, _config);
        var request = _codec.EncodePairRead(no);

        ConsoleFormatter.Info($"[COMPANY] 0x51 读配对信息 NO={no} ← {channel.Describe}");
        ConsoleFormatter.Info($"[COMPANY] TX {request.Length}B: {Hex(request)}");

        var resp = await requester.RequestAsync(channel, request, CompanyCmd.PairInfo);
        if (resp == null)
        {
            ConsoleFormatter.Error("0x51 读无应答（超时）");
            return 1;
        }

        ConsoleFormatter.Info($"[COMPANY] RX: {Hex(CompanyFrame.Build(CompanyFrame.BaseCmdResponse, CompanyCmd.PairInfo, resp))}");

        if (!_codec.TryParsePairReadResp(resp, out var status, out var info))
        {
            ConsoleFormatter.Error("0x51 读应答结构非法（长度不足）");
            return 1;
        }

        if (status != CompanyStatus.Ok || info == null)
        {
            ConsoleFormatter.Error($"STATUS=0x{status:X2}：读取失败（未定义/无配对信息）");
            return 1;
        }

        ConsoleFormatter.Success($"NO={info.No}  BT_MAC={MacAddress.FormatDisplay(info.MacDisplay6)}  BT_NAME=\"{info.BtName}\"");
        return 0;
    }

    /// <summary>
    /// `company pair write`：`0x51` 写配对信息。默认基站 USB；`--port` 走串口（**笔端恒回 STATUS=01**，
    /// 公司 §2.2 明文规定不支持写——不重试、按预期判读；06 文档 T4）。
    /// </summary>
    public async Task<int> PairWriteAsync(string? port, byte no, string macDisplay, string? btName)
    {
        var channel = await ResolveChannelAsync(port);
        if (channel == null)
            return 1;

        byte[] mac;
        try
        {
            mac = MacAddress.ParseDisplay(macDisplay);
        }
        catch (Exception ex)
        {
            ConsoleFormatter.Error($"MAC 解析失败：{ex.Message}");
            return 1;
        }

        var requester = new CommandRequester(_clock, _config);
        var request = _codec.EncodePairWrite(no, mac, btName ?? string.Empty);

        ConsoleFormatter.Info($"[COMPANY] 0x51 写配对信息 → {channel.Describe}" +
                              $"（NO={no} MAC={MacAddress.FormatDisplay(mac)} NAME=\"{btName}\"）");
        ConsoleFormatter.Info($"[COMPANY] TX {request.Length}B（LEN=0x{request[3]:X2}）: {Hex(request)}");

        var resp = await requester.RequestAsync(channel, request, CompanyCmd.PairInfo);
        if (resp == null)
        {
            ConsoleFormatter.Error("0x51 写无应答（超时）");
            return 1;
        }

        ConsoleFormatter.Info($"[COMPANY] RX: {Hex(CompanyFrame.Build(CompanyFrame.BaseCmdResponse, CompanyCmd.PairInfo, resp))}");

        if (!_codec.TryParsePairWriteResp(resp, out var status, out var respNo))
        {
            ConsoleFormatter.Error("0x51 写应答结构非法（长度不足）");
            return 1;
        }

        if (status != CompanyStatus.Ok)
        {
            var hint = channel.Kind == ChannelKind.Serial && status == CompanyStatus.ParamError
                ? "（笔端不支持写，公司 §2.2 明文规定 —— 属预期，06 文档 T4 判据）"
                : string.Empty;
            ConsoleFormatter.Error($"STATUS=0x{status:X2}（{CompanyPairFsm.PairWriteStatusText(status)}），NO={respNo} {hint}");
            return 1;
        }

        ConsoleFormatter.Success($"写入成功（NO={respNo}）");
        return 0;
    }

    /// <summary>`company pair enter`：`0x52` 进配对（USB 基站；`01` → 等待 → `02/03`）。</summary>
    public async Task<int> PairEnterAsync()
    {
        if (!await EnsureUsbAsync())
            return 1;

        var fsm = CreatePairFsm();
        ConsoleFormatter.Info($"[COMPANY] 0x52 进配对 → {_usbChannel.Describe}（等待窗口 {_config.PairingWindowMs}ms）");

        var result = await fsm.EnterPairingAsync(_usbChannel);

        if (result.Success)
        {
            ConsoleFormatter.Success(result.Message);
            return 0;
        }

        ConsoleFormatter.Error(result.Message);
        return 1;
    }

    /// <summary>
    /// `company pair factory`：工厂快配对（**串口读笔端 → USB 写基站 → USB 回读校验**，附丁场景）。
    /// </summary>
    public async Task<int> PairFactoryAsync(string? port, byte no)
    {
        if (!await EnsureSerialAsync(port))
            return 1;
        if (!await EnsureUsbAsync())
            return 1;

        var fsm = CreatePairFsm();
        ConsoleFormatter.Info($"[COMPANY] 工厂快配对：{_serialChannel.Describe} → {_usbChannel.Describe}（NO={no}）");

        var result = await fsm.FactoryPairAsync(_serialChannel, _usbChannel, no);

        if (result.Success)
        {
            ConsoleFormatter.Success(result.Message);
            return 0;
        }

        ConsoleFormatter.Error(result.Message);
        return 1;
    }

    /// <summary>
    /// `company ota`：`0x16` 拉模式升级（USB 基站自升级轨 / 串口笔端轨）。
    /// </summary>
    /// <param name="port">指定则走串口（笔端轨）；否则 USB（基站自升级轨）</param>
    /// <param name="fwPath">固件 bin</param>
    /// <param name="target">bs=0x00 基站 / pen=0x01 笔</param>
    /// <param name="sver">目标版本 `M.m.p`。与设备当前版本相同也允许（同版本升级属正常非错误）：
    /// 新固件受理整包重刷；旧固件（skip 宏开）回 DCMD=02 跳过——两者均成功路径</param>
    public async Task<int> OtaAsync(string? port, string fwPath, string target = "bs",
        string sver = "1.0.0", int hver = -1, int chunkGapMs = -1, int abortAfterBlock = -1)
    {
        var channel = await ResolveChannelAsync(port);
        if (channel == null)
            return 1;

        if (!File.Exists(fwPath))
        {
            ConsoleFormatter.Error($"固件文件不存在：{fwPath}");
            return 1;
        }

        var image = await File.ReadAllBytesAsync(fwPath);
        var parts = sver.Split('.',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts.Any(p => !byte.TryParse(p, out _)))
        {
            ConsoleFormatter.Error($"版本格式非法：{sver}（应为 M.m.p，如 1.0.0）");
            return 1;
        }

        var dt = target.ToLowerInvariant() is "pen" or "pencil"
            ? Downtype.Pen
            : Downtype.BaseStation;
        var hv = (byte)(hver >= 0 ? hver : (dt == Downtype.Pen ? _config.HverPen : _config.HverHid));

        // 包名×升级对象错配：注意级不拦截（防呆不防傻——文件名可改，设备侧指纹/容量判据才是真闸）
        if (FirmwareNaming.TargetMismatchNotice(fwPath, dt) is { } mismatch)
            ConsoleFormatter.Warn($"[COMPANY] {mismatch}");

        var fsm = new CompanyOtaFsm(_codec, channel, _clock, _config);
        fsm.Transitioned += (_, t) => ConsoleFormatter.Info($"[FSM] {t.From} → {t.To}：{t.Reason}");
        fsm.Progress += (_, p) => ConsoleFormatter.Info(
            // 进度以**设备侧**为准（设备才是权威；自算仅兜底 —— 红线 4：自算不替代终态）
            $"[OTA] 进度 {DisplayPercent(p),3}%（{p.BlocksDone}/{p.TotalBlocks} 块；设备侧 {p.DevicePercent}%）");
        // 重查/重发（R1/R2）等均为自动恢复路径，走 WARN 不走 ERROR——否则会被误判为升级失败
        fsm.Warning += (_, w) => ConsoleFormatter.Warn($"[OTA] {w}");

        var crc = Crc16CcittFalse.Compute(image);
        ConsoleFormatter.Info($"[COMPANY] 0x16 升级 → {channel.Describe}");
        if (dt == Downtype.Pen && port == null)
            ConsoleFormatter.Info("[COMPANY] 轨道：B1 透传（PC 出片 22×48B/块 → 基站 BLE 搬运+流控 → 笔端自校验/切区/复位；" +
                                  "PC 对链路无感、块语义不变；笔端须已与基站配对，否则不进 DFU）");
        ConsoleFormatter.Info($"[COMPANY] 镜像 {image.Length}B（{(image.Length + 1023) / 1024} 块）" +
                              $" CRC16=0x{crc:X4} 目标={dt}(0x{(byte)dt:X2}) HVER=0x{hv:X2} SVER={sver}");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            var result = await fsm.RunAsync(image, dt, hv,
                byte.Parse(parts[0]), byte.Parse(parts[1]), byte.Parse(parts[2]),
                abortAfterBlock, chunkGapMs, cts.Token);

            if (result.IsTerminalSuccess)
            {
                ConsoleFormatter.Success($"[OTA] {result.Message}（outcome={result.Outcome}）");
                return 0;
            }

            ConsoleFormatter.Error($"[OTA] {result.Message}（outcome={result.Outcome}）");
            return 1;
        }
        catch (OperationCanceledException)
        {
            ConsoleFormatter.Error("[OTA] 已被取消（Ctrl+C）");
            return 1;
        }
    }

    /// <summary>
    /// `company raw`：发任意 HEX 字节并观察应答（异常/边界用例注入：非 F5 头、`BASE_ID≠01`、
    /// `LEN&lt;3`、半帧、未知 `CMDTYPE`、粘包等）。走通道形态（USB 补零 / 串口不补零）。
    /// </summary>
    public async Task<int> RawAsync(string? port, string hex, int waitMs = 600)
    {
        var channel = await ResolveChannelAsync(port);
        if (channel == null)
            return 1;

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(hex.Replace(" ", "").Replace("-", ""));
        }
        catch (FormatException)
        {
            ConsoleFormatter.Error($"HEX 解析失败：{hex}");
            return 1;
        }

        ConsoleFormatter.Info($"[RAW] TX {bytes.Length}B → {channel.Describe}: {Hex(bytes)}");

        // 一段 HEX 里可能含**多帧**（如"开始帧 + 数据片"用于会话中注入）：按 F5 帧边界切分，
        // **每帧一个报告**逐帧发送 —— 与设备侧"每报告一帧"语义一致（Dongle 05 §3 T6-10）。
        // 单帧时等价于"发一帧"；非 F5/长度非法则整段原样发一帧（保持 T6-3 等用例语义）。
        var frames = SplitF5Frames(bytes);
        if (frames.Count == 0)
            frames = new List<byte[]> { bytes };

        if (frames.Count > 1)
            ConsoleFormatter.Info($"[RAW] 识别到 {frames.Count} 帧 → 逐帧发送（每帧一个报告，间隔 {Math.Max(5, _config.ChunkGapMs)}ms）");

        var received = new List<byte[]>();
        void Handler(object? s, byte[] f) => received.Add(f);
        channel.FrameReceived += Handler;

        try
        {
            foreach (var frame in frames)
            {
                await channel.SendAsync(frame);
                if (frames.Count > 1)
                    await Task.Delay(Math.Max(5, _config.ChunkGapMs));   // 帧间留设备处理时间
            }

            await Task.Delay(waitMs);   // 观察窗口
        }
        finally
        {
            channel.FrameReceived -= Handler;
        }

        if (received.Count == 0)
        {
            ConsoleFormatter.Info($"[RAW] RX 无应答（等待 {waitMs}ms）—— 若期望静默丢弃则 PASS");
            return 0;
        }

        foreach (var frame in received)
            ConsoleFormatter.Info($"[RAW] RX {frame.Length}B: {Hex(frame)}");
        return 0;
    }

    /// <summary>
    /// `company pencmd`：经 **`0x54` 统一TX 转发信封**下发产品级运行时命令（协议补充 02；
    /// 08 号 issue）。双形态：**无 `--port` = BLE 中转**（USB 信封 → 基站转发 → 当前连接笔端；
    /// 基站 R1 回执口径：单帧单事务、等笔端 RSP ≤1s、不缓存不重试，笔未连接回 `STATUS=02`）；
    /// **`--port` = 串口直连**（笔端本地终结）。两种形态 PC 视角字节流一致（补充 02 §4.1 无车头收口）。
    /// 动作：`set-k2 &lt;1|2&gt;` / `query-k2` / `send --hex`。
    /// </summary>
    public async Task<int> PenCmdAsync(string? port, string action, string? modeArg, string? hexPayload)
    {
        byte[] payload;
        string describe;

        switch (action)
        {
            case "set-k2":
                if (!byte.TryParse(modeArg, out var mode) || mode is < 1 or > 2)
                {
                    ConsoleFormatter.Error($"K2 方式取值须为 1|2，实得 \"{modeArg}\"");
                    return 1;
                }
                payload = VpTxCommand.BuildSetVoiceKeyMode(mode);
                describe = $"0x21 SET_VOICE_KEY_MODE → K2 方式{mode}";
                break;

            case "query-k2":
                payload = VpTxCommand.BuildQueryVoiceKeyMode();
                describe = "0x42 QUERY_VOICE_KEY_MODE";
                break;

            case "send":
                if (string.IsNullOrWhiteSpace(hexPayload))
                {
                    ConsoleFormatter.Error("send 需要载荷：--hex \"21 01\"（cmd_id 开头，≤57B）");
                    return 1;
                }
                try
                {
                    payload = Convert.FromHexString(hexPayload.Replace(" ", "").Replace("-", ""));
                }
                catch (FormatException)
                {
                    ConsoleFormatter.Error($"HEX 解析失败：{hexPayload}");
                    return 1;
                }
                describe = $"自定义载荷 [{Convert.ToHexString(payload)}]（透传，语义归统一TX §7）";
                break;

            default:
                ConsoleFormatter.Error($"未知 pencmd 动作：{action}（可用：set-k2 <1|2> | query-k2 | send --hex）");
                return 1;
        }

        var channel = await ResolveChannelAsync(port);
        if (channel == null)
            return 1;

        // 形态超时（各端宏控，pc-issues/03 P-3）：串口直连 1000ms；BLE 中转 2000ms（> 基站等笔端 RSP 的 1s + 裕量）
        var penCmd = new CompanyPenCmdService(_codec, channel, _clock, _config);
        var service = new F5EnvelopeVpCommand(penCmd);
        ConsoleFormatter.Info($"[COMPANY] 0x54 信封 {describe} → {channel.Describe}" +
                              $"（载荷 {payload.Length}B，等待 {penCmd.TimeoutMs}ms）");
        if (port == null)
            ConsoleFormatter.Info("[COMPANY] 轨道：BLE 中转（信封载荷原样写当前连接笔端 CMD 特征，基站不解析；" +
                                  "笔未连接 → STATUS=02；升级会话期间照常可用）");

        PenCmdResult result;
        try
        {
            result = await service.SendRawAsync(payload);
        }
        catch (OperationCanceledException)
        {
            ConsoleFormatter.Error("[COMPANY] 已被取消（Ctrl+C）");
            return 1;
        }

        if (result.EnvelopeStatus is { } env)
            ConsoleFormatter.Info($"[COMPANY] RX: E1 01 54 STATUS=0x{env:X2}" +
                                  (result.CmdId is { } cid
                                      ? $" cmd=0x{cid:X2} pen_status=0x{result.PenStatus:X2}"
                                      : "") +
                                  (result.Payload.Length > 0 ? $" data={Convert.ToHexString(result.Payload)}" : ""));
        else
            ConsoleFormatter.Info($"[COMPANY] RX: 无应答");

        if (!result.Success)
        {
            ConsoleFormatter.Error($"[COMPANY] {describe} 失败：{result.Message}");
            return 1;
        }

        ConsoleFormatter.Success(action switch
        {
            "set-k2" => $"K2 启动方式已设为 方式{modeArg}",
            "query-k2" => result.Payload.Length >= 1
                ? $"K2 启动方式 = 方式{result.Payload[0]}"
                : "查询成功（RSP 无数据字段）",
            _ => $"成功（RSP data={Convert.ToHexString(result.Payload)}）"
        });
        return 0;
    }

    /// <summary>
    /// `company status`：`0x53` 状态上行（协议补充 05）——拉取快照 + 可选监听基站主动推送。
    /// **仅 USB 基站轨**（基站把统一TX 状态槽原样搬运上行，"只搬运不解析"；笔端串口轨零改动不实现）。
    /// UI/重连后主动拉取一次（拉管对齐），实时性靠推送（推管实时）。
    /// </summary>
    /// <param name="port">必须为空——0x53 为基站上行命令，串口轨（直连笔端）不支持</param>
    /// <param name="monitorSec">&gt;0 时拉取后继续监听推送 N 秒（0=拉一次即回）</param>
    public async Task<int> StatusAsync(string? port, int monitorSec)
    {
        if (!string.IsNullOrWhiteSpace(port))
        {
            ConsoleFormatter.Error("0x53 为基站状态上行命令，仅 USB 轨（补充 05：笔端串口轨零改动不实现）——去掉 --port 重试");
            return 1;
        }

        if (!await EnsureUsbAsync())
            return 1;

        var requester = new CommandRequester(_clock, _config);
        var request = _codec.EncodePenStatusPull();

        ConsoleFormatter.Info($"[COMPANY] 0x53 拉取状态快照 ← {_usbChannel.Describe}");
        ConsoleFormatter.Info($"[COMPANY] TX {request.Length}B: {Hex(request)}");

        var resp = await requester.RequestAsync(_usbChannel, request, CompanyCmd.PenStatus);
        if (resp == null)
        {
            ConsoleFormatter.Error($"0x53 无应答（{_config.ResponseTimeoutMs}ms × {_config.MaxRetry + 1} 次）" +
                                   "——基站固件未含状态上行？旧固件对未知 CMDTYPE 静默丢弃");
            return 1;
        }

        ConsoleFormatter.Info($"[COMPANY] RX: {Hex(CompanyFrame.Build(CompanyFrame.BaseCmdResponse, CompanyCmd.PenStatus, resp))}");

        if (!_codec.TryParsePenStatusResp(resp, out var snapshot))
        {
            ConsoleFormatter.Error("0x53 应答结构非法（槽区长度非 2B 整数倍）");
            return 1;
        }

        PrintPenStatus(snapshot, source: "拉取");

        // 拉取 STATUS=02（笔未连接）不提前退出：--monitor 的场景恰是"笔稍后上电/重连"，
        // 建立连接的推送带全量快照自愈（补充 05 §4：推管实时、拉管对齐）。
        if (monitorSec > 0)
            return await MonitorPenStatusAsync(monitorSec, snapshot.Status == PenStatusStatus.Ok);

        return snapshot.Status == PenStatusStatus.Ok ? 0 : 1;
    }

    /// <summary>
    /// `company pair clear`：`0x51 PARAM=03` 清绑定（协议补充 01）——笔端删自身 BLE bond 后
    /// 断连并进配对名学习视窗（60s）。**仅串口/笔端轨**：基站轨明确不实现，且工厂批量写基站时
    /// 同帧广播会误清笔端绑定（§1 禁令）。
    /// </summary>
    public async Task<int> PairClearAsync(string? port)
    {
        if (string.IsNullOrWhiteSpace(port))
        {
            ConsoleFormatter.Error("清绑定仅串口/笔端轨（补充 01 §1：禁止 USB/基站轨发送）——请加 --port COMx");
            return 1;
        }

        if (!await EnsureSerialAsync(port))
            return 1;

        var requester = new CommandRequester(_clock, _config);
        var request = _codec.EncodePairClear();

        ConsoleFormatter.Info($"[COMPANY] 0x51/03 清绑定 → {_serialChannel.Describe}");
        ConsoleFormatter.Info($"[COMPANY] TX {request.Length}B: {Hex(request)}");

        var resp = await requester.RequestAsync(_serialChannel, request, CompanyCmd.PairInfo);
        if (resp == null)
        {
            ConsoleFormatter.Error("0x51/03 无应答（超时）");
            return 1;
        }

        ConsoleFormatter.Info($"[COMPANY] RX: {Hex(CompanyFrame.Build(CompanyFrame.BaseCmdResponse, CompanyCmd.PairInfo, resp))}");

        if (!_codec.TryParsePairClearResp(resp, out var status))
        {
            ConsoleFormatter.Error("0x51/03 应答结构非法（缺 PARAM/STATUS）");
            return 1;
        }

        if (status == PairClearStatus.SessionBusy)
        {
            ConsoleFormatter.Error("STATUS=0xFF：升级会话进行中，拒绝（笔端状态不受影响；会话结束重发即可）");
            return 1;
        }

        if (status != CompanyStatus.Ok)
        {
            ConsoleFormatter.Error($"STATUS=0x{status:X2}（未知值，以笔端口径为准）");
            return 1;
        }

        // 副作用必须显式声明（补充 01 §4：先应答后动作、幂等——未绑定也回 00）
        ConsoleFormatter.Success("清绑定受理（00，幂等）——笔端将执行：删除自身 BLE bond → 断开当前连接" +
                                 " → 进配对名学习视窗（广播 SunVote-R1#，60s）【不复位】");
        ConsoleFormatter.Info("下一步：等待笔端重新广播（ble scan 可见 # 配对态）再续配对流程");
        return 0;
    }

    /// <summary>
    /// 监听 `0x53` 主动推送 N 秒（每帧一报告；Ctrl+C 提前结束）。
    /// <paramref name="sawOk"/>：拉取是否已得有效快照——与推送期任一 STATUS=00 帧合并定退出码
    /// （全程未见过有效快照 → 1，供脚本判读；收到过即 0）。
    /// </summary>
    private async Task<int> MonitorPenStatusAsync(int monitorSec, bool sawOk)
    {
        ConsoleFormatter.Info($"[COMPANY] 监听 0x53 推送 {monitorSec}s（推管实时；Ctrl+C 提前结束）…");
        var requester = new CommandRequester(_clock, _config);
        var deadline = DateTime.UtcNow.AddSeconds(monitorSec);
        int pushes = 0;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(monitorSec));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        while (!cts.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            var remainingMs = (int)Math.Max(100, (deadline - DateTime.UtcNow).TotalMilliseconds);
            var push = await requester.ListenAsync(_usbChannel, CompanyCmd.PenStatus, remainingMs, cts.Token);
            if (push == null)
                break;                       // 超时/取消：静默收尾（推送丢失下一帧自愈）

            if (_codec.TryParsePenStatusResp(push, out var snapshot))
            {
                pushes++;
                if (snapshot.Status == PenStatusStatus.Ok)
                    sawOk = true;
                PrintPenStatus(snapshot, source: $"推送 #{pushes} [{DateTime.Now:HH:mm:ss.fff}]");
            }
        }

        ConsoleFormatter.Info($"监听结束：共 {pushes} 条推送");
        return sawOk ? 0 : 1;
    }

    private static void PrintPenStatus(PenStatusSnapshot s, string source)
    {
        if (s.Status == PenStatusStatus.PenNotConnected)
        {
            ConsoleFormatter.Warn($"[PEN_STATUS] {source}：STATUS=02 笔未连接（无槽区）");
            return;
        }

        if (s.Status != PenStatusStatus.Ok)
        {
            ConsoleFormatter.Warn($"[PEN_STATUS] {source}：STATUS=0x{s.Status:X2}（未知状态，原样显示）");
            return;
        }

        ConsoleFormatter.Success($"[PEN_STATUS] {source}：BYTE2=0x{s.Byte2:X2}" +
                                 $"（chg={s.ChargeState} {PenStatusText.ChargeStateText(s.ChargeState)}，src={s.Src} {PenStatusText.SrcText(s.Src)}）");
        foreach (var slot in s.Slots)
            ConsoleFormatter.Info($"  {PenStatusText.SlotText(slot, s.ChargeState)}");

        ConsoleFormatter.PrintObject(new
        {
            Status = $"0x{s.Status:X2}",
            Byte2 = $"0x{s.Byte2:X2}",
            ChargeState = s.ChargeState,
            ChargeStateText = PenStatusText.ChargeStateText(s.ChargeState),
            Src = s.Src,
            DevState = s.Find(PenStateSlotId.DevState)?.Value,
            BatteryPercent = s.Find(PenStateSlotId.Batt)?.Value,
            RssiDbm = s.RssiDbm,
            SlotCount = s.Slots.Count
        });
    }

    // === 内部 ===

    /// <summary>进度显示值：**优先设备侧**（设备是权威），设备未报时用自算兜底</summary>
    private static int DisplayPercent(OtaProgress p) => p.DevicePercent >= 0 ? p.DevicePercent : p.Percent;

    /// <summary>
    /// 把一段字节按 **F5 帧边界**切分（`F5 AA AA` + `LEN` ⇒ 每帧 `LEN+4` 字节）。
    /// 用于"一次注入多帧序列"（如 开始帧 + 数据片）：设备侧是**每报告一帧**，
    /// 故须逐帧独立发送，不能把多帧塞进同一个报告。
    /// 若起始非 `F5` 或长度非法/不整齐 → 返回空列表（调用方按"整段发一帧"处理）。
    /// </summary>
    private static List<byte[]> SplitF5Frames(ReadOnlySpan<byte> buffer)
    {
        var frames = new List<byte[]>();
        int offset = 0;

        while (offset + CompanyFrame.HeaderLength <= buffer.Length)
        {
            if (buffer[offset] != CompanyFrame.Head0 ||
                buffer[offset + 1] != CompanyFrame.Head1 ||
                buffer[offset + 2] != CompanyFrame.Head2)
                return new List<byte[]>();

            int len = buffer[offset + 3];
            if (len < 4)
                return new List<byte[]>();

            int total = CompanyFrame.HeaderLength + len;
            if (offset + total > buffer.Length)
                return new List<byte[]>();

            frames.Add(buffer.Slice(offset, total).ToArray());
            offset += total;
        }

        return offset == buffer.Length && frames.Count > 0 ? frames : new List<byte[]>();
    }

    private CompanyPairFsm CreatePairFsm()
    {
        var fsm = new CompanyPairFsm(_codec, _clock, _config);
        fsm.Transitioned += (_, t) => ConsoleFormatter.Info($"[FSM] {t.From} → {t.To}：{t.Reason}");
        return fsm;
    }

    /// <summary>按 `--port` 选通道：有端口走串口（笔端），否则走 USB（基站）；不可用返回 null。</summary>
    private async Task<ICommandChannel?> ResolveChannelAsync(string? port)
    {
        if (!string.IsNullOrWhiteSpace(port))
            return await EnsureSerialAsync(port) ? _serialChannel : null;

        return await EnsureUsbAsync() ? _usbChannel : null;
    }

    /// <summary>确保基站 USB 命令通道就绪（0xFF02 无 RID 为主特征，VID/PID 仅弱过滤）。</summary>
    private async Task<bool> EnsureUsbAsync()
    {
        if (_usbChannel.IsOpen)
            return true;

        var devices = await _hidTransport.EnumerateDevicesAsync();
        var match = devices.FirstOrDefault(d =>
                        d.UsagePage == ProtocolConstants.UsagePageCustomHid &&
                        ProtocolConstants.IsTargetVidPid(d.VendorId, d.ProductId))
                    ?? devices.FirstOrDefault(d =>
                        d.IsTargetDongle && ProtocolConstants.IsTargetVidPid(d.VendorId, d.ProductId));

        if (match == null)
        {
            ConsoleFormatter.Error("未找到基站 EP4 命令通道（0xFF02 无 RID）");
            return false;
        }

        if (!await _hidTransport.ConnectAsync(match.DevicePath))
        {
            ConsoleFormatter.Error("连接基站 USB 失败");
            return false;
        }

        if (!_usbChannel.IsOpen)
        {
            ConsoleFormatter.Error($"已连接但非 0xFF02 无 RID 命令通道：{match.InterfaceLabel}");
            return false;
        }

        ConsoleFormatter.Info($"基站命令通道就绪：{match.InterfaceLabel}");
        return true;
    }

    /// <summary>确保笔端串口通道就绪（`115200,N,8,1`；未指定端口时列出可用端口）。</summary>
    private async Task<bool> EnsureSerialAsync(string? port)
    {
        if (_serialChannel.IsOpen)
            return true;

        if (string.IsNullOrWhiteSpace(port))
        {
            ConsoleFormatter.Error("串口未打开：请用 --port COMx 指定笔端串口（工厂快配对需同时连笔端串口与基站 USB）");
            var ports = await _serialTransport.EnumeratePortsAsync();
            if (ports.Count > 0)
                ConsoleFormatter.Info("可用串口：" + string.Join(", ", ports.Select(p => p.PortName)));
            return false;
        }

        var settings = new SerialPortSettings { BaudRate = _config.SerialBaudRate };
        if (!await _serialTransport.ConnectAsync(port, settings))
        {
            ConsoleFormatter.Error($"打开串口失败：{port}");
            return false;
        }

        ConsoleFormatter.Info($"笔端命令通道就绪：{_serialChannel.Describe}");
        return true;
    }

    /// <summary>`MODEL` 位段类别（骨架 §6.2 P5：bit7-4 类别）</summary>
    private static string ModelText(byte model) => (model >> 4) switch
    {
        0x0 => "基站",
        0x1 => "笔端",
        0xF => "通用测试",
        _ => "未知类别"
    };

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);
}
