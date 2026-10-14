// <copyright file="ITuningService.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Protocol.Company;

namespace PencilBox.Core.Services;

/// <summary>
/// `0x61` 内部调参服务（**仅串口轨** PC↔笔端 UART1 直连；基站零感知不转发——issues-pencil/01 §2）。
/// 参数 RAM-only：掉电即失、重启回出厂宏值是特性，本服务不提供持久化语义。
/// </summary>
public interface ITuningService
{
    /// <summary>全表读（`PARAM_ID=0xFF`）：返回笔端上报的 N 条读值（顺序=笔端打包序）</summary>
    Task<IReadOnlyList<TuningParamReading>> ReadAllAsync(string portName, CancellationToken cancellationToken = default);

    /// <summary>单参读；`STATUS≠00` 抛 <see cref="InvalidOperationException"/></summary>
    Task<TuningParamReading> ReadAsync(string portName, byte paramId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 单参写：返回应答（`STATUS` / 生效值）。`STATUS=02`（越界拒收）时
    /// <see cref="TuningWriteResult.Effective"/> = **写前旧值**（笔端不夹取）——调用方据此提示，不视为成功。
    /// </summary>
    Task<TuningWriteResult> WriteAsync(string portName, byte paramId, float value, CancellationToken cancellationToken = default);
}

/// <summary>`0x61/02` 写应答结果</summary>
public sealed record TuningWriteResult(byte Status, byte ParamId, float Effective);
