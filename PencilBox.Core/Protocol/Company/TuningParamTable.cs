// <copyright file="TuningParamTable.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Protocol.Company;

/// <summary>
/// `0x61` 内部调参一条描述：ID / 名称 / 出厂值 / 允许范围 / 单位 / 步长 / 回填宏名 / 调法文案。
/// </summary>
/// <param name="Id">参数 ID（1 起连续）</param>
/// <param name="Name">展示名</param>
/// <param name="Factory">出厂默认（= 固件宏值；协议 RAM-only，重启回此值）</param>
/// <param name="Min">允许下界（闭区间；笔端越界拒收**不夹取**）</param>
/// <param name="Max">允许上界（闭区间）</param>
/// <param name="Unit">单位（灵敏度="dps→px/帧"）</param>
/// <param name="Step">UI 滑条/[−][+] 步进步长（03 卷 §3）</param>
/// <param name="MacroName">固件回填宏名（03 卷 §6 导出片段；与笔端 config 宏名一致）</param>
/// <param name="Guide">内嵌调法文案（03 卷 §3 原文照抄，调参员不需要懂固件）</param>
public sealed record TuningParamDescriptor(
    byte Id, string Name, float Factory, float Min, float Max, string Unit,
    double Step, string MacroName, string Guide)
{
    /// <summary>范围显示文本（`0.05~2.00`）</summary>
    public string RangeText => $"{Format(Min)}~{Format(Max)}";

    /// <summary>数值显示（保留 3 位小数，去尾零交给调用方口径统一）</summary>
    public static string Format(float v) => v.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>宏值显示（float 补一位小数 + f 后缀；整型 ms 不带 f）——03 卷 §6 示例口径：0.35f / 10.0f / 250</summary>
    public string MacroValueText(float v) => Unit == "ms"
        ? v.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
        : v.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture) + "f";
}

/// <summary>
/// `0x61` 内部调参**工具端内置范围表**（issues-pencil/01 §5 参数表 + 03 卷 §3 步长/文案的 PC 侧镜像；
/// 02 卷 §3 单一来源原则：ID/范围/单位/步长/宏名必须与协议表一致，协议表变更须同步本表并重发布）。
/// 用于：`set` 越界提示 / `list` 补齐名称与单位 / `reset` 出厂值 / `export` 宏片段 /
/// CLI JSON 结构化输出 / `空鼠` 工作台卡片（滑条步长、折页文案、★偏离标记）。
/// </summary>
public static class TuningParamTable
{
    /// <summary>首发 6 项（01 卷 §5；顺序即 ID 序；文案=03 卷 §3 原文）</summary>
    public static readonly IReadOnlyList<TuningParamDescriptor> All = new[]
    {
        new TuningParamDescriptor(0x01, "全局灵敏度", 0.3f, 0.05f, 2.00f, "dps→px/帧", 0.05,
            "CONFIG_AIRMOUSE_SENSITIVITY",
            "调大：光标整体变快——同样的手腕动作光标跑得更远，大幅移动省力。\n" +
            "调小：光标整体变慢更稳，但大幅移动要挥更大幅度。\n" +
            "预期换算：陀螺仪每 1 度/秒的转动 ≈ 本值 × 50 像素/秒。\n" +
            "⚠️ 联动：本值改变会让\"小范围精细度\"同步变粗/变细，卡片下方会提示 0x02 的建议补偿值。"),
        new TuningParamDescriptor(0x02, "慢速段增益", 0.27f, 0.05f, 1.00f, "比例", 0.05,
            "CONFIG_AIRMOUSE_SLOW_GAIN",
            "调大：慢慢挪动光标时更跟手（想动就动），但手抖也更容易推动光标——瞄准时容易\"过头\"。\n" +
            "调小：瞄准时手抖几乎推不动光标（稳），但刻意慢速微调会感觉\"重、迟钝\"。\n" +
            "这是\"精确定位稳不稳\"的第一旋钮。"),
        new TuningParamDescriptor(0x03, "曲线拐点", 8.0f, 1.0f, 50.0f, "dps", 1.0,
            "CONFIG_AIRMOUSE_SLOW_KNEE_DPS",
            "调大：更多速度段被划入\"精细区\"（衰减生效区变宽）——中速移动也变稳，但可能感觉整体发滞。\n" +
            "调小：更快进入全速区——整体更跟手，但瞄准微动可能落不进衰减区（手抖直接推光标）。\n" +
            "判断方法：瞄准时手还在轻轻抖、光标却在跑 → 调大；慢速移动觉得拖 → 调小。"),
        new TuningParamDescriptor(0x04, "过渡带宽", 8.0f, 1.0f, 50.0f, "dps", 1.0,
            "CONFIG_AIRMOUSE_SLOW_WIDTH_DPS",
            "调大：快慢之间过渡更柔和，加速时没有\"突然变快\"的换挡感。\n" +
            "调小：快慢切换更干脆，但可能感觉到一个明显的加速转折点。\n" +
            "一般不动它；只有\"移动中有顿挫/换挡感\"时才加宽。"),
        new TuningParamDescriptor(0x05, "软死区", 0.5f, 0.0f, 5.0f, "dps", 0.1,
            "CONFIG_AIRMOUSE_DEADZONE_DPS",
            "调大：手完全静止时光标纹丝不动（稳），但极慢的启动会感觉迟钝（要先用力一点才动）。\n" +
            "调小：极轻微的移动也有响应（跟手），但陀螺仪噪声/漂移可能让光标静止时慢慢爬行。\n" +
            "只在两种情况动它：静置漂移 → 调大；极慢速启动不动 → 调小。"),
        new TuningParamDescriptor(0x06, "释放尾窗", 250f, 0.0f, 1000f, "ms", 25.0,
            "CONFIG_AIRMOUSE_FREEZE_RELEASE_MS",
            "调大：松开按键后光标多钉一会儿——双击更容易点在同一点，但松手后光标\"粘滞\"。\n" +
            "调小：松手即走、更利落，但快速双击的两次落点容易分开（系统不认双击）。\n" +
            "⚠️ 此参数与按键体验线共用，调整前请与固件负责人确认当前按键方案基线。（进阶：一般不动）"),
    };

    /// <summary>按 ID 查描述（未知返回 null）</summary>
    public static TuningParamDescriptor? Find(byte id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>合法 ID 清单文本（`0x01, 0x02, …`，未知 ID 提示用）</summary>
    public static string IdListText => string.Join(", ", All.Select(p => $"0x{p.Id:X2}"));
}
