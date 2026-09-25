// <copyright file="CompanyTargetBindSender.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using Microsoft.Extensions.Logging;
using PencilBox.Core.Ble;
using PencilBox.Core.Fsm;
using PencilBox.Core.Protocol.Company;
using PencilBox.Core.Services;
using PencilBox.Infrastructure.Hid;

namespace PencilBox.Infrastructure.Services;

/// <summary>
/// 协议域「绑定目标下发」实现（**公司 V0.9 F5 协议**）—— 替代旧 4A4C A4/A2 路径。
///
/// 职责（协议域的边界）：
/// - 接收 **显示序 MAC**（6B）+ `BT_NAME`，封装 `0x51` 写配对信息帧 → 经基站 USB 命令通道（EP4 `0xFF02`）下发；
/// - 下发后**回读校验**（`0x51` 读，逐字节比对）——R9 第一验证；
/// - 「名称配对」= `0x52` 进配对：让基站进入 `SunVote-R1#` 学习模式，由基站自行扫描学习笔端。
///
/// **PC 零转换**（冻结基线 R9）：本类不做任何字节序转换——显示序原样进帧，
/// 序转换只在基站内部一处（`f5_mac_reverse`）。旧 A4 要求 PC 转链路序的做法**已退役**。
/// </summary>
public class CompanyTargetBindSender : ITargetBindSender
{
    /// <summary>配对信息组号：基站当前仅接受 `NO=0`（`F5_PAIR_NO_SUPPORTED`）</summary>
    private const byte PairNo = 0x00;

    private readonly HidCommandChannel _channel;
    private readonly ICompanyCodec _codec;
    private readonly CommandRequester _requester;
    private readonly ILogger<CompanyTargetBindSender> _logger;

    public CompanyTargetBindSender(HidCommandChannel channel, ICompanyCodec codec,
        IClock clock, CompanyConfig config, ILogger<CompanyTargetBindSender> logger)
    {
        _channel = channel;
        _codec = codec;
        _requester = new CommandRequester(clock, config);
        _logger = logger;
    }

    public async Task<BindingSendResult> SendBindTargetAsync(byte[] macDisplay6, string? btName,
        CancellationToken cancellationToken = default)
    {
        if (!_channel.IsOpen)
            return new BindingSendResult(false,
                "基站未连接：绑定需先连接接收器 USB 命令通道（0xFF02 无 RID）");

        var displayText = MacAddress.FormatDisplay(macDisplay6);
        var name = string.IsNullOrWhiteSpace(btName) ? BleNaming.BuildBtName(macDisplay6) : btName!;

        try
        {
            // ① 写：0x51 PARAM=02（显示序直传，LEN=0x2B）
            var writeReq = _codec.EncodePairWrite(PairNo, macDisplay6, name);
            _logger.LogInformation("0x51 写配对信息（显示序零转换）: {Mac} name=\"{Name}\"", displayText, name);

            var writeResp = await _requester.RequestAsync(_channel, writeReq, CompanyCmd.PairInfo, cancellationToken)
                .ConfigureAwait(false);

            if (writeResp == null)
                return new BindingSendResult(false, $"0x51 写无应答（超时）— {displayText}；请确认基站已连接且为 EP4 命令通道");

            if (!_codec.TryParsePairWriteResp(writeResp, out var status, out _))
                return new BindingSendResult(false, $"0x51 写应答结构非法 — {displayText}");

            if (status != CompanyStatus.Ok)
                return new BindingSendResult(false,
                    $"0x51 写被拒：STATUS=0x{status:X2}（{PairWriteStatusText(status)}）— {displayText}");

            // ② 回读校验（R9 第一验证：写什么就应读回什么，逐字节一致）
            var readResp = await _requester.RequestAsync(_channel, _codec.EncodePairRead(PairNo),
                CompanyCmd.PairInfo, cancellationToken).ConfigureAwait(false);

            if (readResp == null)
                return new BindingSendResult(false, $"写入成功但回读超时 — {displayText}（可点刷新配对状态复核）");

            if (!_codec.TryParsePairReadResp(readResp, out var readStatus, out var info) || info == null)
                return new BindingSendResult(false,
                    readStatus == CompanyStatus.Ok
                        ? $"回读应答结构非法 — {displayText}"
                        : $"回读失败：STATUS=0x{readStatus:X2} — {displayText}");

            if (!info.MacDisplay6.AsSpan().SequenceEqual(macDisplay6))
                return new BindingSendResult(false,
                    $"回读不一致：写入 {displayText} ↔ 读回 {MacAddress.FormatDisplay(info.MacDisplay6)}（基站侧序转换异常）");

            return new BindingSendResult(true,
                $"绑定成功（0x51）：{displayText} 已写入基站，回读一致");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "0x51 bind dispatch failed");
            return new BindingSendResult(false, $"绑定下发失败：{ex.Message}");
        }
    }

    public async Task<BindingSendResult> StartNamePairingAsync(CancellationToken cancellationToken = default)
    {
        if (!_channel.IsOpen)
            return new BindingSendResult(false,
                "基站未连接：名称配对需先连接接收器 USB 命令通道（0xFF02 无 RID）");

        try
        {
            _logger.LogInformation("0x52 进配对（基站匹配 {PairName}）", BleNameConstants.PairName);

            var resp = await _requester.RequestAsync(_channel, _codec.EncodePairEnter(),
                CompanyCmd.PairEnter, cancellationToken).ConfigureAwait(false);

            if (resp == null)
                return new BindingSendResult(false, "0x52 无应答（超时）；请确认基站已连接且为 EP4 命令通道");

            if (!_codec.TryParsePairEnterResp(resp, out var status, out var paired))
                return new BindingSendResult(false, "0x52 应答结构非法");

            return status switch
            {
                // 01：已进入等待（正常起点）——后续 02/03 是延时结果，由"刷新配对状态"承接
                PairEnterStatus.Entered => new BindingSendResult(true,
                    $"已进入名称配对模式（匹配 {BleNameConstants.PairName}）：请让笔端进入配对视窗，配对结果稍后可点刷新配对状态查看"),

                // 02：基站直接回成功（笔端已在配对态并立刻被学习到）
                PairEnterStatus.Success => new BindingSendResult(true,
                    $"名称配对成功：{DescribePen(paired)}"),

                PairEnterStatus.Failed => new BindingSendResult(false, "名称配对失败：基站回 STATUS=03"),

                _ => new BindingSendResult(false, $"未预期 STATUS=0x{status:X2}（期望 01/02/03）")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "0x52 pair-enter dispatch failed");
            return new BindingSendResult(false, $"名称配对下发失败：{ex.Message}");
        }
    }

    private static string DescribePen(PairInfo? info) =>
        info == null
            ? "（未带笔端信息）"
            : $"{MacAddress.FormatDisplay(info.MacDisplay6)} \"{info.BtName}\"";

    /// <summary>`0x51` 写应答 STATUS 文案（骨架 §6.2）</summary>
    private static string PairWriteStatusText(byte status) => status switch
    {
        CompanyStatus.Ok => "成功",
        CompanyStatus.ParamError => "参数错（笔端不支持写时恒回此值）",
        CompanyStatus.NoOutOfRange => "NO 越界（基站当前仅接受 NO=0）",
        CompanyStatus.Fail => "其它失败",
        _ => $"未知 STATUS 0x{status:X2}"
    };
}
