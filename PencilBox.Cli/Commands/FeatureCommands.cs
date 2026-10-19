// <copyright file="FeatureCommands.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Cli.Output;
using PencilBox.Core.Features;

namespace PencilBox.Cli.Commands;

/// <summary>
/// `feature` 域（14 号架构提案）：功能模块总开关。
/// CLI 与 GUI 功能控制台读同一份 configs/features.json；被关功能的 CLI 域入口拒绝并指路，绝不静默代开。
/// </summary>
public class FeatureCommands
{
    private readonly IFeatureToggleService _toggles;

    public FeatureCommands(IFeatureToggleService toggles)
    {
        _toggles = toggles;
    }

    public int List()
    {
        ConsoleFormatter.PrintTable(
            _toggles.Describe(),
            ("ID", f => f.Id),
            ("名称", f => f.Name),
            ("默认", f => f.DefaultEnabled ? "开" : "关"),
            ("当前", f => _toggles.IsEnabled(f.Id) ? "开" : "关"),
            ("管辖CLI域", f => string.Join(" / ", f.CliDomains)),
            ("说明", f => f.Description));
        ConsoleFormatter.Info("开关=exe 同级 configs/features.json（GUI 功能控制台同一开关）；关闭后 GUI 入口隐藏、CLI 管辖域拒绝。");
        return 0;
    }

    public int Set(string featureId, bool enabled)
    {
        try
        {
            var descriptor = _toggles.GetDescriptor(featureId);
            _toggles.SetEnabled(featureId, enabled);
            if (enabled)
                ConsoleFormatter.Success($"已启用「{descriptor.Name}」（{descriptor.Id}）：GUI 入口恢复、CLI 域 {string.Join(" / ", descriptor.CliDomains)} 可用");
            else
                ConsoleFormatter.Warn($"已停用「{descriptor.Name}」（{descriptor.Id}）：GUI 入口隐藏、CLI 域 {string.Join(" / ", descriptor.CliDomains)} 将拒绝；恢复用 feature on {descriptor.Id}");
            return 0;
        }
        catch (KeyNotFoundException ex)
        {
            ConsoleFormatter.Error(ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// 域门禁（Program 分发处调用）：功能启用 → true 放行；停用 → 打印指路并 false。
    /// 停用属用户有意配置，回 [WARN] 语义（非错误堆栈），退出码由调用方给 1。
    /// </summary>
    public static bool EnsureEnabled(IFeatureToggleService toggles, string featureId)
    {
        if (toggles.IsEnabled(featureId))
            return true;

        var descriptor = toggles.GetDescriptor(featureId);
        ConsoleFormatter.Warn($"「{descriptor.Name}」功能已停用（feature off {descriptor.Id}）：此域入口关闭；恢复执行 pencilbox feature on {descriptor.Id}");
        return false;
    }
}
