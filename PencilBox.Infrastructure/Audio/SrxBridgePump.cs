// <copyright file="SrxBridgePump.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PencilBox.Infrastructure.Audio;

/// <summary>虚拟声卡桥（R1PBleAudio 对外腿的桥式实现，CLI srx bridge 载体）：
/// 把软件接收器解码 PCM（16k/16bit/mono）泵进虚拟声卡的<b>渲染端</b>，
/// 第三方软件从同一虚拟声卡的<b>采集端</b>（如 VB-Cable 的 "CABLE Output"）当麦克风录音。
/// 延迟增量 ≈ 两端 WASAPI 缓冲（典型 15~50ms，可调），只影响第三方消费路，
/// 不进软件接收器主链（识别/扩音/测量全走进程内直供零新增）。
/// Windows 无用户态"创建虚拟麦克风"API，真自研驱动签名成本高——桥路线=适配既有虚拟声卡。</summary>
public sealed class SrxBridgePump : IDisposable
{
    /// <summary>环容量（16k mono 样本）= 1s，吸收 BLE 抖动</summary>
    private const int RingCapacitySamples = 16000;

    private readonly float[] _ring = new float[RingCapacitySamples];
    private int _ringCount;
    private readonly object _lock = new();

    private MMDevice? _renderDevice;
    private WasapiOut? _player;

    public long PumpedSamples { get; private set; }
    public long UnderrunBlocks { get; private set; }

    /// <summary>当前环深 ms（16k mono 口径）</summary>
    public double RingDepthMs => _ringCount / 16.0;

    public string? RenderDeviceName => _renderDevice?.FriendlyName;

    /// <summary>开播：在指定渲染设备上拉环中数据播放（16k mono → 端点混音格式）。</summary>
    public void Start(MMDevice renderDevice, float volume)
    {
        _renderDevice = renderDevice;
        var mixFormat = renderDevice.AudioClient.MixFormat;
        var source = new RingSourceProvider(this);
        var resampled = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(source, mixFormat.SampleRate);
        var gain = new NAudio.Wave.SampleProviders.VolumeSampleProvider(resampled) { Volume = volume };
        ISampleProvider feed = mixFormat.Channels >= 2
            ? new NAudio.Wave.SampleProviders.MonoToStereoSampleProvider(gain)
            : gain;
        _player = new WasapiOut(renderDevice, AudioClientShareMode.Shared, useEventSync: true, 50);
        _player.Init(new NAudio.Wave.SampleProviders.SampleToWaveProvider(feed));
        _player.Play();
    }

    /// <summary>喂解码 PCM（16k/16bit/mono 小端，SoftReceiverAudioFrame.Pcm 直喂）。</summary>
    public void WritePcm(ReadOnlySpan<byte> pcm16le)
    {
        var samples = Pcm16ToFloat(pcm16le);
        lock (_lock)
        {
            var room = RingCapacitySamples - _ringCount;
            var take = Math.Min(samples.Length, room);
            // 满则丢最旧（实时流语义：保新鲜）
            if (take < samples.Length)
            {
                var drop = samples.Length - take;
                if (drop >= _ringCount)
                {
                    _ringCount = 0;
                }
                else
                {
                    Array.Copy(_ring, drop, _ring, 0, _ringCount - drop);
                    _ringCount -= drop;
                    take = Math.Min(samples.Length, RingCapacitySamples - _ringCount);
                }
            }
            Array.Copy(samples, samples.Length - take, _ring, _ringCount, take);
            _ringCount += take;
            PumpedSamples += samples.Length;
        }
    }

    private int ReadFromRing(float[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            var take = Math.Min(count, _ringCount);
            Array.Copy(_ring, 0, buffer, offset, take);
            if (take < _ringCount)
                Array.Copy(_ring, take, _ring, 0, _ringCount - take);
            _ringCount -= take;
            if (take < count)
                UnderrunBlocks++;
            for (var i = take; i < count; i++)
                buffer[offset + i] = 0f;
            return count;
        }
    }

    /// <summary>PCM16LE → float [-1,1)（纯函数，可单测）。</summary>
    public static float[] Pcm16ToFloat(ReadOnlySpan<byte> pcm16le)
    {
        var n = pcm16le.Length / 2;
        var result = new float[n];
        for (var i = 0; i < n; i++)
            result[i] = (short)(pcm16le[i * 2] | (pcm16le[(i * 2) + 1] << 8)) / 32768f;
        return result;
    }

    /// <summary>混音格式字节 → 第一声道 short[]（float32/int16 两编码支持；纯函数可单测）。</summary>
    public static short[] ExtractMonoShort(byte[] raw, WaveFormat fmt)
    {
        var frames = raw.Length / (fmt.Channels * (fmt.BitsPerSample / 8));
        var result = new short[frames];
        var stride = fmt.Channels * (fmt.BitsPerSample / 8);
        for (var f = 0; f < frames; f++)
        {
            var off = f * stride;
            result[f] = fmt.Encoding == WaveFormatEncoding.IeeeFloat
                ? (short)Math.Clamp(BitConverter.ToSingle(raw, off) * 32767f, short.MinValue, short.MaxValue)
                : (short)(raw[off] | (raw[off + 1] << 8));
        }
        return result;
    }

    /// <summary>整数比盒式平均抽取到 16k（纯函数可单测；chirp 频段 ≤3.5k 抗混叠充分）。</summary>
    public static short[] DecimateTo16k(short[] input, int ratio)
    {
        if (ratio <= 1)
            return input;
        var n = input.Length / ratio;
        var result = new short[n];
        for (var i = 0; i < n; i++)
        {
            var acc = 0;
            for (var k = 0; k < ratio; k++)
                acc += input[(i * ratio) + k];
            result[i] = (short)(acc / ratio);
        }
        return result;
    }

    /// <summary>环直读源（WasapiOut 拉取线程；欠载补零保流不断，计欠载块）。</summary>
    private sealed class RingSourceProvider : ISampleProvider
    {
        private readonly SrxBridgePump _pump;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(16000, 1);
        public RingSourceProvider(SrxBridgePump pump) => _pump = pump;
        public int Read(float[] buffer, int offset, int count) => _pump.ReadFromRing(buffer, offset, count);
    }

    /// <summary>非阻塞收尾：快照置空（喂流照旧安全，WritePcm 只碰环），Stop/Dispose 踢后台——
    /// WasapiOut 事件同步模式下 Stop 内部 Join 拉取线程，管道型虚拟设备无消费时事件不发，
    /// 同步等待=调用线程永久卡死（11-03 扩音页死机同族实证）。</summary>
    public void Dispose()
    {
        WasapiOut? player;
        MMDevice? renderDevice;
        lock (_lock)
        {
            player = _player; _player = null;
            renderDevice = _renderDevice; _renderDevice = null;
        }
        if (player == null && renderDevice == null) return;
        _ = Task.Run(() =>
        {
            try
            {
                if (player != null)
                {
                    try { player.Stop(); } catch { /* 收尾容错 */ }
                    player.Dispose();
                }
                renderDevice?.Dispose();
            }
            catch { /* 后台收尾绝不抛 */ }
        });
    }

    // ---- 虚拟声卡配对发现（纯名字算法，单测覆盖；MMDevice 枚举由调用方注入名字表） ----

    /// <summary>
    /// 从渲染/采集设备名表中找"同一虚拟声卡的配对端点"（如 CABLE Input↔CABLE Output）。
    /// 纯函数：入参=设备名列表，出参=(渲染下标, 采集下标)（-1=未找到），可单测。
    /// 启发式：关键词命中虚拟类 + 候选词根配对（去 Input/Output 尾缀的前缀，或括注同族——
    /// 中文驱动常见 "扬声器 (UU远程虚拟音频设备)"↔"麦克风 (UU远程虚拟音频设备)"）。
    /// </summary>
    public static (int Render, int Capture) SelectPair(
        IReadOnlyList<string> renderNames, IReadOnlyList<string> captureNames)
    {
        var renderHit = FindVirtualBest(renderNames);
        var captureHit = FindVirtualBest(captureNames);
        if (renderHit < 0 || captureHit < 0)
            return (-1, -1);
        return NameCandidatesMatch(renderNames[renderHit], captureNames[captureHit])
            ? (renderHit, captureHit)
            : (-1, -1);
    }

    /// <summary>
    /// 虚拟设备里挑最优：**非 UU 外壳优先**（UU远程=第三方软件装的壳，依赖其客户端转发，
    /// 指挥官 11-02 裁决不作为 PencilBox 桥目标）；只有 UU 时仍返回（调用方给警告，不静默挂错）。
    /// </summary>
    private static int FindVirtualBest(IReadOnlyList<string> names)
    {
        int nonUu = -1, firstUu = -1;
        for (var i = 0; i < names.Count; i++)
        {
            if (!IsVirtualDeviceName(names[i]))
                continue;
            if (IsUuShellName(names[i]))
            {
                if (firstUu < 0)
                    firstUu = i;
            }
            else if (nonUu < 0)
            {
                nonUu = i;
            }
        }
        return nonUu >= 0 ? nonUu : firstUu;
    }

    /// <summary>UU 远程外壳判据（纯函数，可单测）：名字含 "uu"（UU远程虚拟音频设备）。</summary>
    public static bool IsUuShellName(string name) =>
        name.Contains("uu", StringComparison.OrdinalIgnoreCase);

    /// <summary>两设备名候选词根有交集即视为同族（纯函数，可单测）。</summary>
    public static bool NameCandidatesMatch(string a, string b)
    {
        var ac = NameCandidates(a);
        var bc = NameCandidates(b);
        return ac.Any(x => x.Length > 0 && bc.Any(y => string.Equals(x, y, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>配对候选词根（纯函数，可单测）：①括注内容（驱动同族名）②去 Input/Output/Playback/Recording
    /// 尾缀、再去 扬声器/麦克风/Speaker/Mic 前缀后的裸名。</summary>
    public static string[] NameCandidates(string name)
    {
        var candidates = new List<string>();
        var paren = name.IndexOf('(');
        var close = name.LastIndexOf(')');
        if (paren > 0 && close > paren)
            candidates.Add(name[(paren + 1)..close].Trim());
        var stripped = (paren > 0 ? name[..paren] : name).Trim();
        foreach (var tail in new[] { "Input", "Output", "Playback", "Recording" })
        {
            if (stripped.EndsWith(tail, StringComparison.OrdinalIgnoreCase) && stripped.Length > tail.Length)
            {
                stripped = stripped[..^tail.Length].Trim();
                break;
            }
        }
        foreach (var lead in new[] { "扬声器", "麦克风", "Speakers", "Speaker", "Microphone", "Mic" })
        {
            if (stripped.StartsWith(lead, StringComparison.OrdinalIgnoreCase) && stripped.Length > lead.Length)
            {
                stripped = stripped[lead.Length..].Trim();
                break;
            }
        }
        candidates.Add(stripped);
        return [.. candidates];
    }

    /// <summary>虚拟声卡名判据（纯函数，可单测）：cable/virtual/voicemeeter/uu虚拟 等。</summary>
    public static bool IsVirtualDeviceName(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.Contains("cable") || lower.Contains("virtual") ||
               lower.Contains("voicemeeter") || lower.Contains("虚拟");
    }

    /// <summary>配对词根（纯函数，可单测）：去尾缀 Input/Output/Playback/Recording 与括号注记后 trim。</summary>
    public static string NormalizeBaseName(string name)
    {
        var paren = name.IndexOf('(');
        var baseName = paren > 0 ? name[..paren] : name;
        baseName = baseName.Trim();
        foreach (var tail in new[] { "Input", "Output", "Playback", "Recording" })
        {
            if (baseName.EndsWith(tail, StringComparison.OrdinalIgnoreCase) && baseName.Length > tail.Length)
            {
                baseName = baseName[..^tail.Length].Trim();
                break;
            }
        }
        return baseName;
    }
}
