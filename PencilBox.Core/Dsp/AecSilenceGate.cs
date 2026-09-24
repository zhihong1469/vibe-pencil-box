namespace PencilBox.Core.Dsp;

/// <summary>
/// AEC 条件触发门（实验特性，默认关）：参考信号静音时旁路 AEC，参考一响立即重开。
/// 判据取<b>参考信号</b>（实际要播出的声音）而非麦克风——参考静音 = 不可能产生新回声，旁路安全；
/// 麦克风静音恰恰相反：那一段里装的往往全是回声，绝不可作判据。
/// 保持窗（hangover）防拖尾：声音停止后，混响与缓冲拖尾里的回声仍要压住一段时间，
/// 立即旁路会把尾巴原样放出来；因此静音须持续满保持窗才关门，任何响动立即开门。
/// </summary>
public sealed class AecSilenceGate
{
    /// <summary>静音判阈（PCM16 帧 RMS，约 -44 dBFS）：参考是数字播放信号，真静音远低于此。</summary>
    public const short SilenceRmsThreshold = 200;

    private readonly int _hangoverFrames;
    private int _remain;

    /// <param name="hangoverFrames">静音保持帧数（10ms/帧），默认 50 帧 = 500ms。</param>
    public AecSilenceGate(int hangoverFrames = 50)
    {
        _hangoverFrames = Math.Max(1, hangoverFrames);
        _remain = _hangoverFrames;   // 冷启动即开门，避免启动瞬间放回声
    }

    /// <summary>每参考帧调用一次。返回 true = AEC 应开启；false = 参考已静音满保持窗，可旁路。</summary>
    public bool Update(short rms)
    {
        if (rms >= SilenceRmsThreshold)
        {
            _remain = _hangoverFrames;
            return true;
        }
        if (_remain > 0)
        {
            _remain--;
            return true;
        }
        return false;
    }

    /// <summary>复位为开门态（新会话/重新启用时先给满保持窗，避免启动瞬间放回声）。</summary>
    public void Reset() => _remain = _hangoverFrames;
}
