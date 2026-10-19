// <copyright file="ISyntheticKeyboard.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.SoftTrigger;

namespace PencilBox.Core.Services;

/// <summary>
/// 合成键盘发腿（SendInput 封装抽象）：软触发回放与 CLI selftest 注入共用。
/// 抽象成接口=引擎逻辑可用假发腿单测（真 SendInput 只在 Windows 交互会话有意义）。
/// </summary>
public interface ISyntheticKeyboard
{
    /// <summary>
    /// 回放键序列（每步：修饰键 DOWN → 主键 DOWN/短暂保持/UP → 修饰键逆序 UP；每步回放【前】先等 <see cref="SoftTriggerStep.DelayMsBefore"/> ms）。
    /// 周期穿插在步间（v2：每步自带延时）；同步阻塞调用，调用方自行决定线程。
    /// </summary>
    void SendSequence(IReadOnlyList<SoftTriggerStep> steps);
}
