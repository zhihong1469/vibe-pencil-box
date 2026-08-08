namespace PencilBox.Core.Communication;

/// <summary>
/// 串口数据接收事件参数
/// </summary>
public class SerialDataReceivedEventArgs : EventArgs
{
    /// <summary>
    /// 接收到的原始字节数据
    /// </summary>
    public byte[] Data { get; }

    /// <summary>
    /// 接收时间戳
    /// </summary>
    public DateTime Timestamp { get; }

    /// <summary>
    /// 数据长度
    /// </summary>
    public int Length => Data.Length;

    public SerialDataReceivedEventArgs(byte[] data)
    {
        Data = data;
        Timestamp = DateTime.Now;
    }
}
