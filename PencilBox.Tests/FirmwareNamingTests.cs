// <copyright file="FirmwareNamingTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Protocol.Company;

namespace PencilBox.Tests;

/// <summary>
/// 固件包命名（版本管理定稿：`&lt;target&gt;_X.Y.Z_&lt;build_tag&gt;_&lt;env&gt;_&lt;shorthash&gt;.bin`）
/// × 升级对象错配提示——**注意级不拦截**（防呆不防傻：文件名可改，真拦截不合适；设备侧判据才是真闸）。
/// </summary>
public class FirmwareNamingTests
{
    [Theory]
    [InlineData("R1P_1.0.1_dev_win_d6baf49.bin")]
    [InlineData(@"input\pencil\R1P_1.0.1_dev_win_d6baf49.bin")]
    public void PenPackage_OnBaseStationTrack_Notices(string path)
    {
        var notice = FirmwareNaming.TargetMismatchNotice(path, Downtype.BaseStation);

        Assert.NotNull(notice);
        Assert.Contains("R1P_", notice);
        Assert.Contains("不拦截", notice);
    }

    [Theory]
    [InlineData("R1D_1.0.1_rc.1_win_8f1d122.bin")]
    [InlineData(@"input\Dongle\R1D_1.0.1_release_win_56bc221.bin")]
    public void DonglePackage_OnPenTrack_Notices(string path)
    {
        var notice = FirmwareNaming.TargetMismatchNotice(path, Downtype.Pen);

        Assert.NotNull(notice);
        Assert.Contains("R1D_", notice);
    }

    [Theory]
    [InlineData("R1P_1.0.1_dev_win_d6baf49.bin", Downtype.Pen)]        // 配对正确：零提示
    [InlineData("R1D_1.0.1_release_win_56bc221.bin", Downtype.BaseStation)]
    [InlineData("pen_r1_v10_v1.0.1_target_20261015_gcc.bin", Downtype.Pen)]   // 旧命名：不判
    [InlineData("ble_app_rc_r1_v10_v0.12.83_twostate-final_gdf58845.bin", Downtype.Pen)]
    [InlineData("app.bin", Downtype.Pen)]                              // 非标命名：不判（不冒充）
    [InlineData("R1X_1.0.0_dev_gcc_abcdef0.bin", Downtype.Pen)]        // 未知 target 前缀：不判
    public void MatchedOrUnjudgeableNames_ReturnNull(string path, Downtype target)
    {
        Assert.Null(FirmwareNaming.TargetMismatchNotice(path, target));
    }
}
