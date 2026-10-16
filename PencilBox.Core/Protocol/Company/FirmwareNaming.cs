// <copyright file="FirmwareNaming.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Protocol.Company;

/// <summary>
/// 固件包命名（版本管理定稿 2026-10）与升级对象的**错配注意级提示**。
///
/// 命名规范 `<target>_<ext_ver>_<build_tag>_<build_env>_<short_hash>.bin`：`R1P_` = 键盘（笔端轨）、
/// `R1D_` = 基站（基站轨）。选包与升级对象不符时给**注意**、**不做拦截**（用户裁定 2026-10-16：
/// 防呆不防傻——文件名可改，真拦不住绕行也不合适；设备侧指纹/容量判据才是真闸）。
/// 旧命名（`pen_r1_*` 等）与非标命名不判（返回 null，不冒充判断）。
/// </summary>
public static class FirmwareNaming
{
    /// <summary>
    /// 包名 target 前缀与升级对象错配时的注意文案；<paramref name="fileName"/> 仅取文件名部分。
    /// 不错配 / 前缀无法识别 → null（调用方零提示）。
    /// </summary>
    public static string? TargetMismatchNotice(string fileName, Downtype target)
    {
        var name = Path.GetFileName(fileName);
        var prefix = name.Length >= 4 && name[3] == '_'
            ? name[..3].ToUpperInvariant()
            : null;

        return (prefix, target) switch
        {
            ("R1P", Downtype.BaseStation) =>
                "包名 R1P_ 为键盘（笔端）固件，当前升级对象=基站——若非故意刷错测试请核对（仅提示不拦截）",
            ("R1D", Downtype.Pen) =>
                "包名 R1D_ 为基站固件，当前升级对象=笔端——若非故意刷错测试请核对（仅提示不拦截）",
            _ => null
        };
    }
}
