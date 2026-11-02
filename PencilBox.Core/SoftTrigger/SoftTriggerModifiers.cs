// <copyright file="SoftTriggerModifiers.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.SoftTrigger;

/// <summary>
/// 修饰键编码 ↔ Win32 VK 集（触发匹配与回放注入共用单点，v2.4 左右侧别）。
/// ushort：低 4 位=通用（任意一侧，HID 字节同款），高字节=侧别（左 0x0100 段/右 0x1000 段）。
/// 存储 JSON 兼容：旧 byte 值即低 4 位，反序列化为 ushort 无需迁移。
/// 边界：笔端设备注入的修饰键只有通用位（固件协议无侧别）——侧别精确匹配只对物理键盘有意义。
/// </summary>
public static class SoftTriggerModifiers
{
    // 通用（任意一侧）
    public const ushort Ctrl = 0x01, Shift = 0x02, Alt = 0x04, Win = 0x08;
    // 侧别（高字节）
    public const ushort LCtrl = 0x0100, LShift = 0x0200, LAlt = 0x0400, LWin = 0x0800;
    public const ushort RCtrl = 0x1000, RShift = 0x2000, RAlt = 0x4000, RWin = 0x8000;

    // 每通道（C/S/A/W）的 Win32 VK：通用=聚合查询键（系统聚合左右），左/右=扩展键
    private static readonly (ushort Generic, ushort Left, ushort Right, int VkGeneric, int VkLeft, int VkRight)[] Channels =
    [
        (Ctrl,   LCtrl,  RCtrl,  0x11, 0xA2, 0xA3),
        (Shift,  LShift, RShift, 0x10, 0xA0, 0xA1),
        (Alt,    LAlt,   RAlt,   0x12, 0xA4, 0xA5),
        (Win,    LWin,   RWin,   0x5B, 0x5B, 0x5C),
    ];

    /// <summary>
    /// 合法性：同通道不得"通用+侧别"混设（LCtrl+Ctrl 冗余歧义，拒）；其余组合（含左右同按）合法。
    /// </summary>
    public static bool IsValid(ushort m)
    {
        foreach (var (generic, left, right, _, _, _) in Channels)
        {
            if ((m & generic) != 0 && ((m & left) != 0 || (m & right) != 0))
                return false;
        }
        return true;
    }

    /// <summary>回放注入用的 DOWN 键序（UP 按调用方逆序）；通用=系统通用 VK，侧别=扩展 VK。</summary>
    public static int[] DownVks(ushort m)
    {
        var vks = new List<int>(4);
        foreach (var (generic, left, right, vkGeneric, vkLeft, vkRight) in Channels)
        {
            if ((m & generic) != 0) vks.Add(vkGeneric);
            else if ((m & left) != 0) vks.Add(vkLeft);
            else if ((m & right) != 0) vks.Add(vkRight);
        }
        return [.. vks];
    }

    /// <summary>
    /// 触发精确匹配（纯函数，键态由 <paramref name="isDown"/> 供给=可注入测试）：
    /// 要求的都按着（通用=左右任一；侧别=该侧按着且**另一侧没按**）、无要求的通道整通道没按。
    /// </summary>
    public static bool Matches(ushort required, Func<int, bool> isDown)
    {
        foreach (var (generic, left, right, vkGeneric, vkLeft, vkRight) in Channels)
        {
            var reqGeneric = (required & generic) != 0;
            var reqLeft = (required & left) != 0;
            var reqRight = (required & right) != 0;

            if (!reqGeneric && !reqLeft && !reqRight)
            {
                // 无要求通道：整通道必须没按（聚合键=防打字误触的"其余都没按"）
                if (isDown(vkGeneric))
                    return false;
                continue;
            }
            if (reqGeneric)
            {
                if (!isDown(vkGeneric))
                    return false;
            }
            else if (reqLeft)
            {
                if (!isDown(vkLeft) || isDown(vkRight))
                    return false;
            }
            else if (reqRight)
            {
                if (!isDown(vkRight) || isDown(vkLeft))
                    return false;
            }
        }
        return true;
    }

    /// <summary>可读文本：通用="Ctrl"，侧别="左Ctrl/右Ctrl"（GUI/CLI 回显共用）。</summary>
    public static string Describe(ushort m)
    {
        var parts = new List<string>(4);
        foreach (var (generic, left, right, _, _, _) in Channels)
        {
            var name = generic == Ctrl ? "Ctrl" : generic == Shift ? "Shift" : generic == Alt ? "Alt" : "Win";
            if ((m & generic) != 0) parts.Add(name);
            else if ((m & left) != 0) parts.Add("左" + name);
            else if ((m & right) != 0) parts.Add("右" + name);
        }
        return string.Join("+", parts);
    }
}
