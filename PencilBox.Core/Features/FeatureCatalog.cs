// <copyright file="FeatureCatalog.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Features;

/// <summary>
/// 可选功能静态目录（14 号架构提案）。
///
/// 域划分铁律（指挥官 2026-10-19 二轮裁决）：
/// - **设备强相关域不进本表**（Keymap/连接/绑定/OTA/workstate）——它们跟"连接"同生共死，不可关；
///   Keymap 编辑留在仪表盘原位（裁剪时砍的就是整个设备域，不存在"Keymap 窗口在、连接砍不掉"的矛盾）。
/// - 进表的都是**可关的增值功能**；动作库（12/13 号，纯软件域）进场时在此加一行。
///
/// DefaultEnabled 全 true：老用户零行为变化；产品版默认开关表（发行级）在此逐项改。
/// </summary>
public static class FeatureCatalog
{
    public static readonly IReadOnlyList<FeatureDescriptor> All =
    [
        new FeatureDescriptor(
            Id: "airmouse",
            Name: "空鼠调参与遥测",
            Description: "0x61 内部调参 / 0x62 遥测流与零漂统计（GUI 空鼠工作台；须串口直连笔端）",
            DefaultEnabled: true,
            CliDomains: ["company param", "company stream", "company record", "company stillstats"]),

        new FeatureDescriptor(
            Id: "audio",
            Name: "扩音与音频工具",
            Description: "扩音直通回路、录音/播放/延迟快测等音频工具（GUI 扩音页）",
            DefaultEnabled: true,
            CliDomains: ["audio"]),

        new FeatureDescriptor(
            Id: "seriallog",
            Name: "串口抓日志与对拍",
            Description: "设备日志 + F5 帧统一时间轴、遥测解析、对拍摘要（研发向工具；GUI 串口页）",
            DefaultEnabled: true,
            CliDomains: ["serial"]),

        // 动作库键盘族（13 号，2026-10-19 落地）：纯软件域第一户——零设备依赖，
        // "软触发单功能发行版"（指挥官愿景）将来只留此模块。
        new FeatureDescriptor(
            Id: "softtrigger",
            Name: "软触发（键盘动作族）",
            Description: "专用键 F17~F24 触发键盘序列回放（连点/热键编排；纯上位机软件，不插设备也全功能）",
            DefaultEnabled: true,
            CliDomains: ["softtrigger"]),
    ];

    public static FeatureDescriptor? Find(string featureId) =>
        All.FirstOrDefault(f => string.Equals(f.Id, featureId, StringComparison.OrdinalIgnoreCase));
}
