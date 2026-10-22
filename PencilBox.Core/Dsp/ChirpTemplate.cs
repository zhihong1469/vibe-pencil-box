// <copyright file="ChirpTemplate.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Dsp;

/// <summary>
/// 2b chirp 模板（规格单 t7/Dongle/D2 §1 定版，固件 chirp_tab[120] 同源整数表，Box 优先用表）：
/// 线性 up-chirp 1k→3kHz / 7.5ms=120 样点 @16k mono s16 / 幅值 12000 / 周期 750ms=12000 样点。
/// probe 的 1k→8k 扫频模板（PulseTrain）与之独立，两模板不混用。
/// </summary>
public static class ChirpTemplate
{
    public const int SampleRate = 16000;
    public const int LengthSamples = 120;
    public const int PeriodSamples = 12000;   // 750ms；固件窗 = chirp_cnt % 12000 < 120
    public const int Amplitude = 12000;

    public static readonly short[] Table =
    [
        0, 4628, 8596, 11217, 11984, 10674,
        7429, 2763, -2495, -7336, -10762, -12000,
        -10692, -7022, -1722, 4043, 8918, 11678,
        11549, 8457, 3106, -3144, -8596, -11678,
        -11413, -7763, -1722, 4917, 10064, 12000,
        9978, 4556, -2495, -8731, -11876, -10674,
        -5448, 1916, 8596, 11902, 10392, 4556,
        -3257, -9731, -11984, -8891, -1722, 6303,
        11413, 11071, 5307, -3144, -10064, -11846,
        -7429, 981, 8918, 11995, 8373, -39,
        -8485, -12000, -8373, 353, 8918, 11960,
        7429, -1916, -10064, -11581, -5307, 4628,
        11413, 10211, 1722, -8059, -11984, -7022,
        3257, 11102, 10392, 1527, -8596, -11846,
        -5448, 5483, 11876, 8232, -2495, -11102,
        -9978, 39, 10064, 10947, 1722, -9150,
        -11413, -2763, 8596, 11581, 3106, -8513,
        -11549, -2763, 8918, 11299, 1722, -9731,
        -10692, 39, 10762, 9496, -2495, -11678,
        -7429, 5483, 11984, 4264, -8596, -11071,
    ];
}
