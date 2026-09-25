// <copyright file="ITargetBindSender.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Services;

/// <summary>
/// 「绑定目标下发」接口 —— 协议层之间的解耦边界（**公司 V0.9 F5 协议口径**）。
///
/// 职责划分（并列协议层的约定）：
/// - BLE/串口域（取 MAC）：只负责拿到目标笔端的 **显示序 MAC**，
///   然后调用本接口把它填进下发；不关心帧格式、通道、补零/定长等细节；
/// - 协议域（本接口实现方）：负责构造公司协议帧（`0x51` 写配对信息 / `0x52` 进配对）
///   → 经命令通道（USB HID / 串口）**真正下发**。
///
/// **MAC 一律显示序（PC 零转换）**：冻结基线 R9 —— 线上显示序直传，
/// 序转换只在基站内部一处完成；PC 侧任何位置都不得再反转字节序。
///
/// 语义：处理的是「绑定」（基站 ↔ 笔端 1:1），非蓝牙断开/重连。
/// </summary>
public interface ITargetBindSender
{
    /// <summary>
    /// 把选中笔端绑定到基站（`0x51` 写配对信息，写入基站配对表）。
    /// </summary>
    /// <param name="macDisplay6">目标笔端的 **显示序** MAC（6B，高字节在前；PC 零转换）。</param>
    /// <param name="btName">
    /// 协议 `BT_NAME`（32B 字段的字符串形态，如 `SunVote-R1:C656003A5DF1`）；
    /// 传空则实现方按 MAC 自行拼接。
    /// </param>
    /// <param name="cancellationToken">取消令牌</param>
    Task<BindingSendResult> SendBindTargetAsync(byte[] macDisplay6, string? btName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 让基站进入**名称配对**模式（`0x52` 进配对，匹配 `SunVote-R1#` 广播）。
    /// 用于"用户配对模式"：基站自行扫描并学习进入配对视窗的笔端。
    /// </summary>
    Task<BindingSendResult> StartNamePairingAsync(CancellationToken cancellationToken = default);
}
