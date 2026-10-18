// <copyright file="IGlobalKeyboardHook.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Services;

/// <summary>
/// 全局键盘钩子（笔端工作状态自管的观测腿；产品落地/byte2…/PC 落地回执 §三 立案，2026-10-18）。
///
/// Win32 低级键盘钩子（WH_KEYBOARD_LL）实现，**被动监听不吞键**（CallNextHookEx 恒转发）——
/// 外部消费软件用同键做热键不受影响。事件在钩子线程触发，订阅方自行调度。
/// </summary>
public interface IGlobalKeyboardHook
{
    /// <summary>任意键按下边沿（长按自动重复不重发；参数=Win32 虚键码 VK）。在钩子线程触发。</summary>
    event Action<int>? KeyDown;

    /// <summary>安装钩子并启动消息泵线程（幂等；重复调用无副作用）。</summary>
    void Start();

    /// <summary>卸载钩子并停线程（幂等）。</summary>
    void Stop();
}
