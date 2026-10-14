// <copyright file="AirMouseMacroSnippet.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Text;

namespace PencilBox.Core.Protocol.Company;

/// <summary>
/// 空鼠调参**导出宏片段**生成器（03 卷 §6 核心交付；CLI `company param export` 与
/// Desktop 空鼠工作台「导出宏片段」按钮共用本单点，文案/宏名/格式单一来源）。
/// 产物直接粘贴回笔端 config 宏出正式包固化（协议侧零参与，纯固件流程）。
/// </summary>
public static class AirMouseMacroSnippet
{
    /// <summary>
    /// 生成回填片段：头部注释（导出时间 + 笔固件版本 + 备注）+ 每参数一行 `#define`。
    /// <paramref name="readings"/> 缺某 ID 时该宏以注释行标"未读到"（不产出半截 define 误导回填）；
    /// 未知 ID（前向兼容）原样注释附尾。
    /// </summary>
    public static string Build(
        IReadOnlyList<TuningParamReading> readings,
        string? firmwareVersion,
        string? note,
        DateTime? localTime = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// ===== 空鼠调参宏片段（PencilBox 导出，粘贴回 config 宏后出包固化） =====");
        sb.AppendLine($"// 导出时间: {(localTime ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm:ss")}");
        sb.AppendLine($"// 笔固件: {(!string.IsNullOrWhiteSpace(firmwareVersion) ? firmwareVersion : "未知（0x03 无应答）")}");
        if (!string.IsNullOrWhiteSpace(note))
            sb.AppendLine($"// 备注: {note.Trim()}");
        sb.AppendLine();

        var byId = new Dictionary<byte, float>();
        foreach (var r in readings)
            byId[r.Id] = r.Value;

        foreach (var d in TuningParamTable.All)
        {
            if (byId.TryGetValue(d.Id, out var v))
                sb.AppendLine($"#define {d.MacroName} {d.MacroValueText(v)}");
            else
                sb.AppendLine($"// #define {d.MacroName} ……（0x{d.Id:X2} {d.Name} 未读到，跳过——请先全表读确认笔端在位）");
        }

        var unknown = readings.Where(r => TuningParamTable.Find(r.Id) == null).ToList();
        if (unknown.Count > 0)
        {
            sb.AppendLine();
            foreach (var u in unknown)
                sb.AppendLine($"// 未知参数 0x{u.Id:X2} = {TuningParamDescriptor.Format(u.Value)}（工具表未收录，原样附注）");
        }

        return sb.ToString();
    }
}
