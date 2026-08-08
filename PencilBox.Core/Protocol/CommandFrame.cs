namespace PencilBox.Core.Protocol;

/// <summary>
/// HID 命令帧构建器
/// 帧结构: [HEAD_TAG(2)] [VERSION(1)] [CHANNEL(1)] [LENGTH(2)] [CMD(1)] [DATA(N)] [TAIL_TAG(1)]
/// </summary>
public static class CommandFrame
{
    /// <summary>
    /// 头标识: "JL" (0x4A 0x4C)
    /// </summary>
    public const byte HeadTag1 = 0x4A;
    public const byte HeadTag2 = 0x4C;

    /// <summary>
    /// 尾标识: 0xED
    /// </summary>
    public const byte TailTag = 0xED;

    /// <summary>
    /// 协议版本
    /// </summary>
    public const byte ProtocolVersion = 0x00;

    /// <summary>
    /// 帧头最小长度 (HEAD + VERSION + CHANNEL + LENGTH + CMD)
    /// </summary>
    public const int HeaderLength = 7;

    /// <summary>
    /// 帧脚长度 (TAIL)
    /// </summary>
    public const int FooterLength = 1;

    /// <summary>
    /// 最大数据载荷长度
    /// </summary>
    public const int MaxDataLength = 56; // 64 - 8 (header + footer)

    /// <summary>
    /// 构建命令帧
    /// </summary>
    /// <param name="channel">通信通道</param>
    /// <param name="commandId">命令 ID</param>
    /// <param name="data">数据载荷（可为空）</param>
    /// <returns>完整的命令帧字节数组</returns>
    public static byte[] Build(HidChannel channel, CommandId commandId, byte[]? data = null)
    {
        data ??= Array.Empty<byte>();

        if (data.Length > MaxDataLength)
            throw new ArgumentException($"Data length {data.Length} exceeds maximum {MaxDataLength}");

        int totalLength = HeaderLength + data.Length + FooterLength;
        byte[] frame = new byte[totalLength];

        // HEAD TAG
        frame[0] = HeadTag1;
        frame[1] = HeadTag2;

        // VERSION
        frame[2] = ProtocolVersion;

        // CHANNEL
        frame[3] = (byte)channel;

        // LENGTH (data length, high byte first)
        frame[4] = (byte)((data.Length >> 8) & 0xFF);
        frame[5] = (byte)(data.Length & 0xFF);

        // COMMAND ID
        frame[6] = (byte)commandId;

        // DATA
        if (data.Length > 0)
        {
            Array.Copy(data, 0, frame, 7, data.Length);
        }

        // TAIL TAG
        frame[7 + data.Length] = TailTag;

        return frame;
    }

    /// <summary>
    /// 解析命令帧
    /// </summary>
    /// <param name="rawData">原始 HID Report 数据</param>
    /// <param name="reportId">输出: Report ID</param>
    /// <param name="channel">输出: 通道号</param>
    /// <param name="commandId">输出: 命令 ID</param>
    /// <param name="payload">输出: 数据载荷</param>
    /// <returns>是否解析成功</returns>
    public static bool TryParse(byte[] rawData, out byte reportId, out HidChannel channel,
        out CommandId commandId, out byte[] payload)
    {
        reportId = 0;
        channel = HidChannel.PcToDongle;
        commandId = CommandId.RcspData;
        payload = Array.Empty<byte>();

        // rawData 包含 1 字节 Report ID + 帧头 + 数据 + 帧脚
        if (rawData.Length < HeaderLength + 1 + FooterLength)
            return false;

        reportId = rawData[0];

        // 检查头标识 (frame[0..1] = rawData[1..2])
        if (rawData[1] != HeadTag1 || rawData[2] != HeadTag2)
            return false;

        // 通道号 (frame[3] = rawData[4])
        channel = (HidChannel)rawData[4];

        // 数据长度 (frame[4..5] = rawData[5..6])
        int dataLength = (rawData[5] << 8) | rawData[6];

        if (dataLength < 0 || dataLength > MaxDataLength)
            return false;

        // 命令 ID (frame[6] = rawData[7])
        commandId = (CommandId)rawData[7];

        // 检查尾标识 (frame[7+dataLength] = rawData[8+dataLength])
        int tailIndex = HeaderLength + 1 + dataLength;
        if (tailIndex >= rawData.Length || rawData[tailIndex] != TailTag)
            return false;

        // 提取数据 (frame[7..] = rawData[8..])
        if (dataLength > 0)
        {
            payload = new byte[dataLength];
            Array.Copy(rawData, HeaderLength + 1, payload, 0, dataLength);
        }

        return true;
    }

    /// <summary>
    /// 验证帧完整性
    /// </summary>
    public static bool IsValidFrame(byte[] frame)
    {
        if (frame.Length < HeaderLength + FooterLength)
            return false;

        if (frame[0] != HeadTag1 || frame[1] != HeadTag2)
            return false;

        int dataLength = (frame[4] << 8) | frame[5];
        if (dataLength < 0 || dataLength > MaxDataLength)
            return false;

        int tailIndex = HeaderLength + dataLength;
        return tailIndex < frame.Length && frame[tailIndex] == TailTag;
    }
}
