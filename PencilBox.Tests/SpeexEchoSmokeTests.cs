// <copyright file="SpeexEchoSmokeTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Infrastructure.Audio;

namespace PencilBox.Tests;

/// <summary>
/// SpeexDSP 回声消除集成冒烟：合成已知回声路径（延迟 30ms + 增益 0.5），收敛后残余回声
/// 能量压制量（ERLE）必须显著（≥10dB）。这是"效果不低于教科书解法"的最低门槛验收。
/// </summary>
public class SpeexEchoSmokeTests
{
    [Fact]
    public void EchoCancel_ReducesSyntheticEchoByAtLeast10dB()
    {
        const int frame = 160, rate = 16000, delay = 480, seconds = 5;
        using var proc = new SpeexEchoProcessor(frame, 1600, rate);
        var rng = new Random(7);

        // 远端参考：一阶低通白噪（近似语音谱）；mic = 延迟 480 样本 × 0.5 的回声 + 微弱独立近端
        var lp = 0.0;
        var refStream = new List<short>(rate * seconds);
        var echoStream = new List<short>(rate * seconds);
        for (var i = 0; i < rate * seconds; i++)
        {
            lp = 0.9 * lp + 0.1 * rng.Next(-12000, 12000);
            var v = (short)Math.Clamp(lp, -32767, 32767);
            refStream.Add(v);
            echoStream.Add(i >= delay ? (short)(refStream[i - delay] * 0.5) : (short)0);
        }

        double micEnergy = 0, outEnergy = 0;
        var counted = 0;
        for (var off = 0; off + frame <= rate * seconds; off += frame)
        {
            var mic = new short[frame];
            for (var i = 0; i < frame; i++)
                mic[i] = (short)(echoStream[off + i] + rng.Next(-50, 50));

            // 参考按真实延迟对齐（backOffset = delay，与引擎同款取法）
            var reference = new short[frame];
            for (var i = 0; i < frame; i++)
            {
                var pos = off - delay + i;
                reference[i] = pos >= 0 ? refStream[pos] : (short)0;
            }

            var output = new short[frame];
            proc.Process(mic, reference, output);

            if (off >= rate * 3)   // 前 3 秒留给自适应收敛
            {
                for (var i = 0; i < frame; i++)
                {
                    micEnergy += (double)mic[i] * mic[i];
                    outEnergy += (double)output[i] * output[i];
                }
                counted++;
            }
        }

        Assert.True(counted > 100, "收敛后样本不足");
        var erleDb = 10.0 * Math.Log10(micEnergy / Math.Max(1.0, outEnergy));
        Assert.True(erleDb >= 10.0, $"ERLE 仅 {erleDb:F1} dB（要求 ≥10dB）——回声未被有效压制");
    }
}
