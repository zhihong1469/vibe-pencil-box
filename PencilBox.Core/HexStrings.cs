// <copyright file="HexStrings.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Text;

namespace PencilBox.Core;

/// <summary>
/// 字节序列 → 大写十六进制串（单源双框架工具）。
/// net10 走 <see cref="Convert.ToHexString"/>；net48（外发裁剪小工具 OtaCore）无此 API，走 StringBuilder。
/// </summary>
public static class HexStrings
{
    public static string ToHex(ReadOnlySpan<byte> bytes)
    {
#if NET48
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
            sb.Append(b.ToString("X2"));
        return sb.ToString();
#else
        return Convert.ToHexString(bytes);
#endif
    }
}
