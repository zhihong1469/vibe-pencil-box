using NAudio.Wave;

namespace PencilBox.Infrastructure.Audio;

/// <summary>WAV 读取结果（已混成单声道 16-bit，附原始采样率）</summary>
public sealed record WavMono(short[] Mono, int SampleRate, int Channels, int BitsPerSample);

/// <summary>
/// 最小 WAV 读写（NAudio WaveFileReader/Writer 包一层）：
/// 读侧只认 16-bit PCM 与 32-bit float（本工具自录与他端录音的两种现实格式），
/// 多声道混成单声道；写侧固定 16-bit PCM。
/// </summary>
public static class WavFile
{
    public static WavMono ReadMono(string path)
    {
        using var reader = new WaveFileReader(path);
        var fmt = reader.WaveFormat;
        var bytes = new byte[reader.Length];
        var read = 0;
        while (read < bytes.Length)
        {
            var n = reader.Read(bytes, read, bytes.Length - read);
            if (n <= 0) break;
            read += n;
        }

        var isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat;
        if (!isFloat && fmt.Encoding != WaveFormatEncoding.Pcm)
            throw new NotSupportedException($"不支持的 WAV 编码: {fmt.Encoding}（仅 PCM16/IEEE float）");

        var ch = fmt.Channels;
        short[] mono;
        if (isFloat && fmt.BitsPerSample == 32)
        {
            var frames = (int)(read / (4 * ch));
            mono = new short[frames];
            for (var f = 0; f < frames; f++)
            {
                double acc = 0;
                for (var c = 0; c < ch; c++)
                    acc += BitConverter.ToSingle(bytes, f * 4 * ch + c * 4);
                mono[f] = (short)Math.Clamp(acc / ch * 32768, short.MinValue, short.MaxValue);
            }
        }
        else if (fmt.BitsPerSample == 16)
        {
            var frames = (int)(read / (2 * ch));
            mono = new short[frames];
            for (var f = 0; f < frames; f++)
            {
                var acc = 0;
                for (var c = 0; c < ch; c++)
                    acc += BitConverter.ToInt16(bytes, f * 2 * ch + c * 2);
                mono[f] = (short)(acc / ch);
            }
        }
        else
        {
            throw new NotSupportedException($"不支持的位深: {fmt.BitsPerSample}bit（仅 16/32float）");
        }

        return new WavMono(mono, fmt.SampleRate, ch, fmt.BitsPerSample);
    }

    public static void WriteMono16(string path, short[] mono, int sampleRate)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var writer = new WaveFileWriter(path, new WaveFormat(sampleRate, 16, 1));
        var bytes = new byte[mono.Length * 2];
        Buffer.BlockCopy(mono, 0, bytes, 0, bytes.Length);
        writer.Write(bytes, 0, bytes.Length);
        writer.Flush();
    }
}
