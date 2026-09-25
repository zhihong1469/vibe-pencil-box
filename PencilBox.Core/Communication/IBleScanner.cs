// <copyright file="IBleScanner.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Communication;

/// <summary>
/// BLE 广播扫描服务（上位机侧）。解耦设计：
/// - 接口在 Core（纯逻辑），实现平台相关部分（Windows WinRT 蓝牙广播）在 Infrastructure；
/// - 供 CLI `ble` 命令域 / `device ep4 send --ble` 使用，扫描并据此给 Dongle 传递目标 MAC（免手输）。
/// </summary>
public interface IBleScanner
{
    /// <summary>
    /// 扫描指定时长的 BLE 广播，返回去重后的设备列表（已识别 SunVote 笔端并填好显示序/链路序）。
    /// </summary>
    /// <param name="durationSec">扫描秒数</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task<IReadOnlyList<BleDeviceInfo>> ScanAsync(int durationSec = 5,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 扫描并推荐一个最合适的目标（配对态优先 + RSSI 降序），供 A4/裸 6B 注入。
    /// </summary>
    /// <param name="durationSec">扫描秒数</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task<BleDeviceInfo?> RecommendAsync(int durationSec = 5,
        CancellationToken cancellationToken = default);
}
