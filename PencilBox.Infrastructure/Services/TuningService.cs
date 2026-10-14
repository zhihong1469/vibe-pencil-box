// <copyright file="TuningService.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using Microsoft.Extensions.Logging;
using PencilBox.Core.Ble;
using PencilBox.Core.Communication;
using PencilBox.Core.Configuration;
using PencilBox.Core.Fsm;
using PencilBox.Core.Protocol.Company;
using PencilBox.Core.Services;
using PencilBox.Infrastructure.Serial;

namespace PencilBox.Infrastructure.Services;

/// <summary>
/// `0x61` 内部调参服务实现（串口轨专用，串口开启模式同 <see cref="PenBindingService"/>）。
/// 超时/重发取 <see cref="CompanyConfig"/> 通用值（500ms × ≤3 重发——01 卷 §6 建议值恰好一致）。
/// </summary>
public class TuningService : ITuningService
{
    private readonly ICompanyCodec _codec;
    private readonly ISerialTransport _serialTransport;
    private readonly SerialCommandChannel _serialChannel;
    private readonly CommandRequester _requester;
    private readonly CompanyConfig _config;
    private readonly ILogger<TuningService> _logger;

    public TuningService(ICompanyCodec codec, ISerialTransport serialTransport,
        SerialCommandChannel serialChannel, IClock clock, CompanyConfig config,
        ILogger<TuningService> logger)
    {
        _codec = codec;
        _serialTransport = serialTransport;
        _serialChannel = serialChannel;
        _requester = new CommandRequester(clock, config);
        _config = config;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TuningParamReading>> ReadAllAsync(string portName,
        CancellationToken cancellationToken = default)
    {
        await EnsureSerialOpenAsync(portName, cancellationToken);

        var resp = await RequestAsync(portName, _codec.EncodeTuningRead(TuningParamMode.ParamIdAll), cancellationToken);
        if (!_codec.TryParseTuningReadAllResp(resp, out var status, out var items))
            throw new InvalidOperationException("0x61 全表读应答结构非法（缺 STATUS/COUNT 或载荷不足）");

        if (status != TuningStatus.Ok)
            throw new InvalidOperationException($"0x61 全表读失败：STATUS=0x{status:X2}");

        _logger.LogInformation("0x61 全表读（串口 {Port}）→ {Count} 项", portName, items.Count);
        return items;
    }

    public async Task<TuningParamReading> ReadAsync(string portName, byte paramId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSerialOpenAsync(portName, cancellationToken);

        var resp = await RequestAsync(portName, _codec.EncodeTuningRead(paramId), cancellationToken);
        if (!_codec.TryParseTuningReadResp(resp, out var status, out var echoId, out var value))
            throw new InvalidOperationException("0x61 单参读应答结构非法（缺 STATUS/PARAM_ID/VALUE4）");

        if (status != TuningStatus.Ok)
            throw new InvalidOperationException(
                status == TuningStatus.UnknownId
                    ? $"0x61 未知参数 ID=0x{paramId:X2}（合法：{TuningParamTable.IdListText}）"
                    : $"0x61 单参读失败：STATUS=0x{status:X2}");

        return new TuningParamReading(echoId, value);
    }

    public async Task<TuningWriteResult> WriteAsync(string portName, byte paramId, float value,
        CancellationToken cancellationToken = default)
    {
        await EnsureSerialOpenAsync(portName, cancellationToken);

        var resp = await RequestAsync(portName, _codec.EncodeTuningWrite(paramId, value), cancellationToken);
        if (!_codec.TryParseTuningWriteResp(resp, out var status, out var echoId, out var effective))
            throw new InvalidOperationException("0x61 写应答结构非法（缺 STATUS/PARAM_ID/VALUE4）");

        _logger.LogInformation("0x61 写参数 {Id}={Value}（串口 {Port}）→ STATUS=0x{Status:X2} 生效={Effective}",
            paramId, value, portName, status, effective);
        return new TuningWriteResult(status, echoId, effective);
    }

    /// <summary>发送并等 `0x61` 应答；无应答抛 <see cref="TuningNoAnswerException"/>（UI 按类型分流，勿以消息文本判链路）</summary>
    private async Task<byte[]> RequestAsync(string portName, byte[] request, CancellationToken ct)
    {
        var resp = await _requester.RequestAsync(_serialChannel, request, CompanyCmd.TuningParam, ct)
            .ConfigureAwait(false);
        return resp ?? throw new TuningNoAnswerException(
            $"0x61 无应答（{portName} 超时）：serial list / serial probe 确认端口在位；" +
            "占用串口的程序先关（PencilBox.Desktop / sscom 教训）；并确认笔端固件已含 0x61 支持");
    }

    /// <summary>确保目标串口已打开（端口不同则重连）；同 <see cref="PenBindingService"/> 口径</summary>
    private async Task EnsureSerialOpenAsync(string portName, CancellationToken cancellationToken)
    {
        if (_serialTransport.IsConnected &&
            string.Equals(_serialTransport.CurrentPortName, portName, StringComparison.OrdinalIgnoreCase))
            return;

        var settings = new SerialPortSettings { BaudRate = _config.SerialBaudRate };
        if (!await _serialTransport.ConnectAsync(portName, settings, cancellationToken))
            throw new InvalidOperationException($"打开串口失败：{portName}（可能被占用或端口号有误）");
    }
}
