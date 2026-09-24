namespace PencilBox.Core.Protocol;

/// <summary>
/// 私有命令帧构建器（对齐 link/host/usb/01_帧格式与传输约定.md）
///
/// 帧布局：
/// <code>
/// +--------+--------+----------+--------+-----------+-----------+------+---------------+-------+
/// |  0x4A  |  0x4C  | Channel  | 保留   | Length_H  | Length_L  | CMD  |   Data...     | 0xED  |
/// |  [0]   |  [1]   |   [2]    |  [3]   |    [4]    |    [5]    | [6]  |   [7..6+DL]   | 7+DL  |
/// +--------+--------+----------+--------+-----------+-----------+------+---------------+-------+
/// </code>
///
/// - 头标识 0x4A 0x4C（'J''L'）
/// - Channel：通道号，bit3-0 携带协议版本（现=0）
/// - Length：大端序，= Data 字节数 + 1（含 CMD 字节）
/// - CMD：命令 ID；CMD=0xFF 时 Data[0]=Sub-CMD
/// - 帧总长 = Length + 7 = DL + 8
/// </summary>
public static class CommandFrame
{
    /// <summary>头标识字节 1: 'J'（引用统一常量源）</summary>
    public const byte HeadTag1 = ProtocolConstants.CmdHeadH;

    /// <summary>头标识字节 2: 'L'（引用统一常量源）</summary>
    public const byte HeadTag2 = ProtocolConstants.CmdHeadL;

    /// <summary>尾标识（引用统一常量源）</summary>
    public const byte TailTag = ProtocolConstants.CmdTail;

    /// <summary>协议版本（嵌入 Channel 的 bit3-0，当前=0）</summary>
    public const byte ProtocolVersion = ProtocolConstants.CmdProtocolVersion;

    /// <summary>帧头长度（HEAD 2 + CHANNEL 1 + RESERVED 1 + LENGTH 2 + CMD 1）</summary>
    public const int HeaderLength = ProtocolConstants.CmdHeaderLength;

    /// <summary>帧脚长度（TAIL）</summary>
    public const int FooterLength = ProtocolConstants.CmdFooterLength;

    /// <summary>
    /// 最大数据载荷长度（DL 上限）。引用统一常量源（D1 决策：63-8=55）。
    /// </summary>
    public const int MaxDataLength = ProtocolConstants.MaxCmdDataLength;

    /// <summary>
    /// 构建命令帧
    /// </summary>
    /// <param name="channel">通信通道</param>
    /// <param name="commandId">命令 ID</param>
    /// <param name="data">数据载荷（不含 CMD 字节，可为空）</param>
    /// <returns>完整命令帧字节数组（不含 HID Report ID）</returns>
    public static byte[] Build(HidChannel channel, CommandId commandId, byte[]? data = null)
    {
        data ??= Array.Empty<byte>();

        if (data.Length > MaxDataLength)
            throw new ArgumentException($"Data length {data.Length} exceeds maximum {MaxDataLength}");

        // 帧总长 = Header(7) + Data + Footer(1) = Data + 8
        int totalLength = HeaderLength + data.Length + FooterLength;
        var frame = new byte[totalLength];

        frame[0] = HeadTag1;
        frame[1] = HeadTag2;

        // Channel（bit3-0 携带协议版本，当前=0）
        frame[2] = (byte)(((byte)channel & 0xF0) | (ProtocolVersion & 0x0F));

        // 保留
        frame[3] = 0x00;

        // Length = Data 字节数 + 1（含 CMD）
        int length = data.Length + 1;
        frame[4] = (byte)((length >> 8) & 0xFF);
        frame[5] = (byte)(length & 0xFF);

        // CMD
        frame[6] = (byte)commandId;

        // Data
        if (data.Length > 0)
            Array.Copy(data, 0, frame, 7, data.Length);

        // Tail
        frame[7 + data.Length] = TailTag;

        return frame;
    }

    /// <summary>
    /// 解析命令帧（输入不含 HID Report ID，从 0x4A 开始）
    /// </summary>
    /// <param name="frame">帧字节（不含 Report ID）</param>
    /// <param name="channel">输出：通道号</param>
    /// <param name="commandId">输出：命令 ID</param>
    /// <param name="payload">输出：数据载荷（不含 CMD 字节）</param>
    /// <returns>是否解析成功</returns>
    public static bool TryParse(byte[] frame, out HidChannel channel,
        out CommandId commandId, out byte[] payload)
    {
        channel = HidChannel.PcToDongle;
        commandId = CommandId.RcspData;
        payload = Array.Empty<byte>();

        if (frame == null || frame.Length < HeaderLength + FooterLength)
            return false;

        // 头标识
        if (frame[0] != HeadTag1 || frame[1] != HeadTag2)
            return false;

        // 通道号（去掉版本位）
        channel = (HidChannel)(frame[2] & 0xF0);

        // Length（= Data + 1）
        int length = (frame[4] << 8) | frame[5];
        if (length < 1)
            return false;

        int dataLength = length - 1;
        if (dataLength > MaxDataLength)
            return false;

        // CMD
        commandId = (CommandId)frame[6];

        // 尾标识
        int tailIndex = 7 + dataLength;
        if (tailIndex >= frame.Length || frame[tailIndex] != TailTag)
            return false;

        // Data
        if (dataLength > 0)
        {
            payload = new byte[dataLength];
            Array.Copy(frame, 7, payload, 0, dataLength);
        }

        return true;
    }

    /// <summary>
    /// 验证帧完整性
    /// </summary>
    public static bool IsValidFrame(byte[] frame)
    {
        if (frame == null || frame.Length < HeaderLength + FooterLength)
            return false;
        if (frame[0] != HeadTag1 || frame[1] != HeadTag2)
            return false;
        int length = (frame[4] << 8) | frame[5];
        if (length < 1)
            return false;
        int dataLength = length - 1;
        if (dataLength > MaxDataLength)
            return false;
        int tailIndex = 7 + dataLength;
        return tailIndex < frame.Length && frame[tailIndex] == TailTag;
    }
}
