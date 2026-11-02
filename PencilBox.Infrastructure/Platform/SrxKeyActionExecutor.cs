// <copyright file="SrxKeyActionExecutor.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Ble.SoftReceiver;

namespace PencilBox.Infrastructure.Platform;

/// <summary>
/// 软件键鼠执行器（CLI <c>srx receive/amp --actions</c> 与 Desktop「软件键鼠」开关共用一份逻辑）：
/// 键盘腿 = 0x13 语义键 + S1 档位 → <see cref="SrxKeyActionRouter"/> 查 Keymap 出厂表 → 注入；
/// 鼠标腿 = 空鼠槽原始量直通（相对位移/滚轮/键沿）。注入进前台焦点（标准 SendInput 语义）。
/// 每实例自带 S1 变化沿基线（0xFF 哨兵：首条只建基线防建连 3 连报假切换）与鼠标帧采样
/// （首 8 帧露原始值，空鼠没生效判别用）。所有执行结果经 log/warn 通道上抛（文案无前缀，
/// 由调用方决定 [SRX] 等装饰）。
/// </summary>
public sealed class SrxKeyActionExecutor
{
    private readonly SrxKeyActionRouter _router = SrxKeyActionRouter.FromFactory();
    private readonly SystemInputInjector _injector = new();
    private readonly Action<string>? _log;
    private readonly Action<string>? _warn;
    private byte _lastSwitch = 0xFF;
    private long _mouseSamples;

    public SrxKeyActionExecutor(Action<string>? log = null, Action<string>? warn = null)
    {
        _log = log;
        _warn = warn;
    }

    /// <summary>用已知的当前档位预置 S1 基线（中途开启时防把下次真实切换前的状态当变化）。</summary>
    public void SeedSwitchBaseline(byte mode) => _lastSwitch = mode;

    /// <summary>0x13 语义键：查表注入（无配置/未支持类型日志降级，不抛出）。</summary>
    public void HandleKeyAction(SoftReceiverKeyActionEvent a)
    {
        var entry = _router.Map(a.KeyNum, a.Action);
        if (entry == null)
        {
            _log?.Invoke(
                $"键动作 {SrxKeyActionRouter.KeyName(a.KeyNum)} {SrxKeyActionRouter.ActionName(a.Action)} → 表无此配置，不注入");
            return;
        }
        var taps = a.Action == 0x02 ? 2 : 1;   // DoubleClick 条目命中时 tap 两下
        try
        {
            switch (entry.ActionType)
            {
                case Core.Protocol.KeyActionType.Keyboard:
                    _injector.TapUsage(entry.CodeLo, entry.Modifier, taps);
                    break;
                case Core.Protocol.KeyActionType.Consumer:
                    var vk = SystemInputInjector.ConsumerVk(entry.CodeLo, entry.CodeHi)
                        ?? throw new NotSupportedException(
                            $"Consumer 0x{((entry.CodeHi << 8) | entry.CodeLo):X3} 注入侧未支持");
                    _injector.KeyTap(vk, taps);
                    break;
                default:
                    throw new NotSupportedException($"动作类型 {entry.ActionType} 注入侧未支持");
            }
            _log?.Invoke(
                $"键动作 {SrxKeyActionRouter.KeyName(a.KeyNum)} {SrxKeyActionRouter.ActionName(a.Action)}" +
                $" → {SrxKeyActionRouter.DescribeEntry(entry)} 已注入");
        }
        catch (Exception ex)
        {
            _warn?.Invoke($"键动作注入失败：{ex.Message}");
        }
    }

    /// <summary>S1 档位（0x16）：变化沿才查切换表注入；首条只建基线。</summary>
    public void HandleSwitch(byte mode)
    {
        if (_lastSwitch == 0xFF)
        {
            _lastSwitch = mode;   // 基线
            return;
        }
        if (mode == _lastSwitch)
            return;
        _lastSwitch = mode;
        var entry = _router.MapSwitch(mode);
        if (entry == null)
            return;
        try
        {
            _injector.TapUsage(entry.CodeLo, entry.Modifier, 1);
            _log?.Invoke($"S1 档位 → {mode} 注入 usage 0x{entry.CodeLo:X2}");
        }
        catch (Exception ex)
        {
            _warn?.Invoke($"S1 档位注入失败：{ex.Message}");
        }
    }

    /// <summary>鼠标逐帧：相对位移 + 滚轮直注（零位移不注；首 8 帧采样露原始值）。</summary>
    public void HandleMouseFrame(SoftReceiverMouseFrame m)
    {
        try
        {
            var idx = Interlocked.Increment(ref _mouseSamples);
            if (idx <= 8)
            {
                _log?.Invoke(
                    $"鼠标帧采样#{idx} buttons=0x{m.Buttons:X2} x={m.X} y={m.Y} wheel={m.Wheel}" +
                    (idx == 8 ? "（采样完，后续静默；汇总见状态行 鼠帧/位移）" : ""));
            }
            if (m.X != 0 || m.Y != 0)
                _injector.MouseMoveRel(m.X, m.Y);
            if (m.Wheel != 0)
                _injector.MouseWheel(m.Wheel * 120);   // ±1 格 = ±WHEEL_DELTA
        }
        catch (Exception ex)
        {
            _warn?.Invoke($"鼠标注入失败：{ex.Message}");
        }
    }

    /// <summary>鼠标键沿：左右键状态注入。</summary>
    public void HandleMouseButton(bool left, bool right)
    {
        try
        {
            _injector.MouseButton(true, left);
            _injector.MouseButton(false, right);
            _log?.Invoke($"鼠标键沿 左={(left ? "按下" : "释放")} 右={(right ? "按下" : "释放")}");
        }
        catch (Exception ex)
        {
            _warn?.Invoke($"鼠标键注入失败：{ex.Message}");
        }
    }
}
