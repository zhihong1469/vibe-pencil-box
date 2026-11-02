// <copyright file="ISoftTriggerSettings.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.SoftTrigger;

/// <summary>
/// 连点器全局手感设置（v2.4，2026-11-02 指挥官"等待时长给用户自己调"）：
/// 触发键松开等待上限 ms——引擎回放前等触发键松开（防回放键夹进按压=目标软件热键判定被夹断），
/// 该等待的最长封顶即本值：短按几乎无感；长按=按多久等多久，封顶后照放（不吞动作只隔离时序）。
/// 0=不等（按下立即回放，恢复 v2.2 旧手感但可能夹键）；默认 800。
/// 全局单值（非每定义）——手感是整页统一偏好；引擎每次派发实时读（CLI/GUI 改动即时生效，免重启）。
/// </summary>
public interface ISoftTriggerSettings
{
    /// <summary>触发键松开等待上限 ms（0=不等；合法域 0~2000）。</summary>
    int ReleaseWaitMs { get; }

    /// <summary>设置松开等待上限（越界夹取 0~2000 后落盘）。</summary>
    void SetReleaseWaitMs(int ms);
}
