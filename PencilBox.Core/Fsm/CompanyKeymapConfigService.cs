// <copyright file="CompanyKeymapConfigService.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Communication;
using PencilBox.Core.Models;
using PencilBox.Core.Protocol.Company;

namespace PencilBox.Core.Fsm;

/// <summary>0x55 应用结果（下发腿汇报；探测无应答时后续步骤不执行）。</summary>
public sealed record KeymapApplyReport
{
    /// <summary>元信息探测是否有应答（false=基站未实现 0x55/旧固件——"基站未响应"降级路径）</summary>
    public bool ProbeResponded { get; init; }

    /// <summary>探测应答的元信息（有应答时）</summary>
    public int MetaCount { get; init; }

    public int MetaMaxEntries { get; init; }

    /// <summary>已成功写入的条目数（02 按键条 + 06 切换行）</summary>
    public int Written { get; init; }

    /// <summary>03 提交是否成功（全表落 NVDS）</summary>
    public bool Committed { get; init; }

    /// <summary>失败/中止原因（空=全链成功）</summary>
    public string? Error { get; init; }

    /// <summary>端到端成功（探测+全量写+提交）。</summary>
    public bool AllOk => ProbeResponded && Committed && Error == null;
}

/// <summary>
/// `0x55` 快捷键可配置**会话服务**（pc-issues/05；CLI `keymap apply` / GUI「生效」的下发腿）。
///
/// 交互模型同提案 §3 两段式：探测（01/0xFF 元信息）→ 逐条 02 写 RAM → 06 写切换表 → 03 提交 NVDS。
/// **整表重发是有意的**（2026-10-18 二次定版）：设备表现状未知（可能已被其他表/来源改过），整表覆盖免对账；
/// 增量下发等 0x55 读侧（PARAM 01 逐条读）落地后再议。
///
/// **回执门控**（指挥官 2026-10-18 定版）：本地生效由调用方在 `AllOk` 后自行执行——
/// 探测无应答（旧固件/三端未裁决，现网必走）或中途失败时调用方**不得**切换本地生效配置。
/// 固件落地后同一代码即成为真下发。
/// </summary>
public sealed class CompanyKeymapConfigService
{
    private readonly ICommandChannel _channel;
    private readonly IClock _clock;
    private readonly CompanyConfig _config;

    public CompanyKeymapConfigService(ICommandChannel channel, IClock clock, CompanyConfig config)
    {
        _channel = channel;
        _clock = clock;
        _config = config;
    }

    /// <summary>底层通道是否就绪（UI 据此分流"未连接基站"提示）。</summary>
    public bool IsOpen => _channel.IsOpen;

    /// <summary>
    /// 全表应用：探测 → 逐条写（按键条 slot=配置存储序下标；切换表跳过 mode=0 内部行）→ 提交。
    /// 任一步失败即中止并带 <see cref="KeymapApplyReport.Error"/>。
    /// </summary>
    public async Task<KeymapApplyReport> ApplyAsync(
        IReadOnlyList<KeymapEntry> entries,
        IReadOnlyList<KeymapSwitchEntry> switchEntries,
        CancellationToken cancellationToken = default)
    {
        var requester = new CommandRequester(_clock, _config);

        // ① 探测：基站对未知 CMDTYPE 静默丢弃 → 无应答=null 即"未响应"
        var meta = await requester.RequestAsync(_channel, KeymapConfigFrames.EncodeMetaProbe(),
            CompanyCmd.KeymapConfig, cancellationToken).ConfigureAwait(false);
        if (meta == null || !KeymapConfigFrames.TryParseMeta(meta, out var metaStatus, out var count, out var maxEntries, out _))
            return new KeymapApplyReport { ProbeResponded = false, Error = "探测无应答" };
        if (metaStatus != 0x00)
            return new KeymapApplyReport { ProbeResponded = true, Error = $"元信息读失败 STATUS=0x{metaStatus:X2}" };

        // ② 逐条写按键条（RAM）
        var written = 0;
        for (byte slot = 0; slot < entries.Count; slot++)
        {
            var resp = await requester.RequestAsync(_channel, KeymapConfigFrames.EncodeWriteEntry(slot, entries[slot]),
                CompanyCmd.KeymapConfig, cancellationToken).ConfigureAwait(false);
            if (resp == null || !KeymapConfigFrames.TryParseResponse(resp, out _, out var status))
                return Partial($"写条目 slot={slot} 无应答（已写 {written} 条后中止）", written);
            if (status != 0x00)
                return Partial($"写条目 slot={slot} 拒绝 STATUS=0x{status:X2}", written);
            written++;
        }

        // ③ 写切换表（mode=0 内部行不发——固件应拒写，PC 主动不碰）
        foreach (var sw in switchEntries)
        {
            if (KeymapRules.IsSwitchInternalRow(sw.Mode))
                continue;
            var resp = await requester.RequestAsync(_channel, KeymapConfigFrames.EncodeWriteSwitch(sw),
                CompanyCmd.KeymapConfig, cancellationToken).ConfigureAwait(false);
            if (resp == null || !KeymapConfigFrames.TryParseResponse(resp, out _, out var status))
                return Partial($"写切换表 mode={sw.Mode} 无应答（已写 {written} 条后中止）", written);
            if (status != 0x00)
                return Partial($"写切换表 mode={sw.Mode} 拒绝 STATUS=0x{status:X2}", written);
            written++;
        }

        // ④ 提交 NVDS
        var commit = await requester.RequestAsync(_channel, KeymapConfigFrames.EncodeCommit(),
            CompanyCmd.KeymapConfig, cancellationToken).ConfigureAwait(false);
        if (commit == null || !KeymapConfigFrames.TryParseResponse(commit, out _, out var commitStatus))
            return Partial("提交无应答（RAM 已生效，掉电即失）", written);
        if (commitStatus != 0x00)
            return Partial($"提交拒绝 STATUS=0x{commitStatus:X2}", written);

        return new KeymapApplyReport
        {
            ProbeResponded = true,
            MetaCount = count,
            MetaMaxEntries = maxEntries,
            Written = written,
            Committed = true
        };

        KeymapApplyReport Partial(string error, int writtenCount) => new()
        {
            ProbeResponded = true,
            MetaCount = count,
            MetaMaxEntries = maxEntries,
            Written = writtenCount,
            Error = error
        };
    }
}
