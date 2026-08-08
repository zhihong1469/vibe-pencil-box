namespace PencilBox.Core.Protocol;

/// <summary>
/// PencilBox 协议处理器
/// 负责命令的编码、解码、校验和计算
/// </summary>
public class PencilBoxProtocol
{
    /// <summary>
    /// 单字节 XOR 校验
    /// </summary>
    public static byte CalculateChecksum(byte[] data)
    {
        byte checksum = 0;
        foreach (byte b in data)
        {
            checksum ^= b;
        }
        return checksum;
    }

    /// <summary>
    /// 构建获取 Dongle 信息命令
    /// </summary>
    public static byte[] BuildGetDongleMessage()
    {
        return CommandFrame.Build(HidChannel.PcToDongle, CommandId.GetDongleMessage);
    }

    /// <summary>
    /// 构建获取连接设备列表命令
    /// </summary>
    public static byte[] BuildGetConnectDevice()
    {
        return CommandFrame.Build(HidChannel.PcToDongle, CommandId.GetConnectDevice);
    }

    /// <summary>
    /// 构建断开设备命令
    /// </summary>
    /// <param name="deviceIndex">设备索引</param>
    public static byte[] BuildDisconnectDevice(byte deviceIndex)
    {
        byte[] data = { deviceIndex };
        return CommandFrame.Build(HidChannel.PcToDongle, CommandId.DisconnectDevice, data);
    }

    /// <summary>
    /// 构建重连设备命令
    /// </summary>
    /// <param name="deviceIndex">设备索引</param>
    public static byte[] BuildReconnectDevice(byte deviceIndex)
    {
        byte[] data = { deviceIndex };
        return CommandFrame.Build(HidChannel.PcToDongle, CommandId.ReconnectDevice, data);
    }

    /// <summary>
    /// 构建自定义命令
    /// </summary>
    /// <param name="subCommand">子命令码</param>
    /// <param name="data">命令数据</param>
    public static byte[] BuildCustomCommand(byte subCommand, byte[]? data = null)
    {
        byte[] payload;
        if (data != null && data.Length > 0)
        {
            payload = new byte[1 + data.Length];
            payload[0] = subCommand;
            Array.Copy(data, 0, payload, 1, data.Length);
        }
        else
        {
            payload = new byte[] { subCommand };
        }
        return CommandFrame.Build(HidChannel.PcToDongle, CommandId.Custom, payload);
    }

    /// <summary>
    /// 解析响应帧
    /// </summary>
    /// <param name="reportData">完整的 HID Report 数据</param>
    /// <returns>解析结果，失败返回 null</returns>
    public static DecodedFrame? ParseResponse(byte[] reportData)
    {
        if (CommandFrame.TryParse(reportData, out byte reportId, out HidChannel channel,
            out CommandId commandId, out byte[] payload))
        {
            return new DecodedFrame(reportId, channel, commandId, payload);
        }
        return null;
    }
}

/// <summary>
/// 解析后的帧结构
/// </summary>
public record DecodedFrame(
    byte ReportId,
    HidChannel Channel,
    CommandId CommandId,
    byte[] Payload)
{
    /// <summary>
    /// 获取无符号 16 位参数（大端序）
    /// </summary>
    public ushort GetUInt16(int offset)
    {
        if (offset + 1 >= Payload.Length)
            return 0;
        return (ushort)((Payload[offset] << 8) | Payload[offset + 1]);
    }

    /// <summary>
    /// 获取指定偏移的字节
    /// </summary>
    public byte GetByte(int offset)
    {
        return offset < Payload.Length ? Payload[offset] : (byte)0;
    }
}
