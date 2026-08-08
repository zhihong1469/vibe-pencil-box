namespace PencilBox.Core.Communication;

/// <summary>
/// 串口配置参数
/// </summary>
public class SerialPortSettings
{
    /// <summary>
    /// 波特率
    /// </summary>
    public int BaudRate { get; set; } = 115200;

    /// <summary>
    /// 数据位
    /// </summary>
    public int DataBits { get; set; } = 8;

    /// <summary>
    /// 停止位
    /// </summary>
    public SerialStopBits StopBits { get; set; } = SerialStopBits.One;

    /// <summary>
    /// 校验位
    /// </summary>
    public SerialParity Parity { get; set; } = SerialParity.None;

    /// <summary>
    /// 握手协议
    /// </summary>
    public SerialHandshake Handshake { get; set; } = SerialHandshake.None;

    /// <summary>
    /// 读取超时（毫秒）
    /// </summary>
    public int ReadTimeout { get; set; } = 100;

    /// <summary>
    /// 写入超时（毫秒）
    /// </summary>
    public int WriteTimeout { get; set; } = 100;
}

public enum SerialStopBits
{
    None,
    One,
    OnePointFive,
    Two
}

public enum SerialParity
{
    None,
    Odd,
    Even,
    Mark,
    Space
}

public enum SerialHandshake
{
    None,
    XOnXOff,
    RequestToSend,
    RequestToSendXOnXOff
}
