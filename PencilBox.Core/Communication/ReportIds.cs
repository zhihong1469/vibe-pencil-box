namespace PencilBox.Core.Communication;

/// <summary>
/// USB HID Report ID 常量
/// </summary>
public static class ReportIds
{
    /// <summary>
    /// 键盘 HID 标准报告
    /// </summary>
    public const byte Keyboard = 0x01;

    /// <summary>
    /// Consumer 控制（播放/静音等）
    /// </summary>
    public const byte Consumer = 0x02;

    /// <summary>
    /// 鼠标报告
    /// </summary>
    public const byte Mouse = 0x03;

    /// <summary>
    /// 音频数据（ADPCM 流）
    /// </summary>
    public const byte Audio = 0x04;

    /// <summary>
    /// 音频控制
    /// </summary>
    public const byte AudioControl = 0x05;

    /// <summary>
    /// 自定义命令通道（PC→Dongle）
    /// </summary>
    public const byte CustomCommand = 0xA0;
}
