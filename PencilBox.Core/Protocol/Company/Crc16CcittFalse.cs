// <copyright file="Crc16CcittFalse.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Protocol.Company;

/// <summary>
/// CRC16-CCITT-FALSE（poly `0x1021` / init `0xFFFF` / 无反转 / xorout `0`）。
/// 自旧 `SelfOtaProtocol` 迁入（算法不变），供公司 V0.9 `0x16` 整包校验复用。
/// 已知向量：`"123456789"` → `0x29B1`。
/// </summary>
public static class Crc16CcittFalse
{
    public static ushort Compute(byte[] data) => Compute(data.AsSpan());

    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++)
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        return crc;
    }
}
