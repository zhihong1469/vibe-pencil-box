namespace PencilBox.Core.Protocol.Company;

/// <summary>
/// 公司 V0.9 帧底座（F5 家族）—— **单一 framing profile**（协议约定层的帧部分）。
///
/// 布局：`[F5 AA AA][LEN][BASECMD][BASE_ID=0x01][CMDTYPE][DATA…]`
///   - `LEN` = `LEN` 字节**之后**的负载长度（= `3 + DATA.Length`）；**总逻辑长 = `LEN + 4`**；
///   - USB 形态补零到 64B（补零**不计入** `LEN`）；串口形态不补零（`LEN` 是唯一定帧字段）；
///   - 多字节**大端**；帧内无 CRC。
///
/// `LEN` 一律取**骨架 §6.1 权威表**（0x03=0x04 / 0x51 读=0x05、写=0x2B、读应答=0x2C /
/// 0x52=0x04、应答 02=0x2A）；**禁止照抄公司原件示例**（骨架 §6.2 P1/P2）。
///
/// 依据：骨架 §5/§6.1、冻结基线 §4.1；本端协议正文 `上位机/01 §②`、`上位机/02 §④`。
/// </summary>
public static class CompanyFrame
{
    /// <summary>帧头 `F5 AA AA`</summary>
    public const byte Head0 = 0xF5;
    public const byte Head1 = 0xAA;
    public const byte Head2 = 0xAA;

    /// <summary>帧头长度（`F5 AA AA` + `LEN`）</summary>
    public const int HeaderLength = 4;

    /// <summary>最小合法逻辑帧长（`LEN` 最小 = 4：BASECMD + BASE_ID + CMDTYPE + 1B 负载）</summary>
    public const int MinTotalLength = HeaderLength + 4;

    /// <summary>最大合法逻辑帧长（`LEN` 单字节）</summary>
    public const int MaxTotalLength = HeaderLength + 0xFF;

    /// <summary>`BASE_ID`（冻结基线 §4.1：固定 0x01）</summary>
    public const byte BaseId = 0x01;

    /// <summary>`BASECMD` 下行（PC → 设备）</summary>
    public const byte BaseCmdRequest = 0x61;

    /// <summary>`BASECMD` 上行（设备 → PC）</summary>
    public const byte BaseCmdResponse = 0xE1;

    /// <summary>
    /// 组一帧**逻辑帧**（未补零）；补零与否由通道决定（03 §2 依赖铁律 4）。
    /// </summary>
    /// <param name="baseCmd">`BASECMD`：下行 0x61 / 上行 0xE1</param>
    /// <param name="cmdType">`CMDTYPE`（0x03 / 0x51 / 0x52 / 0x16…）</param>
    /// <param name="data">`CMDTYPE` **之后**的负载（PARAM/NO/…）</param>
    public static byte[] Build(byte baseCmd, byte cmdType, ReadOnlySpan<byte> data)
    {
        int len = 3 + data.Length;   // BASECMD + BASE_ID + CMDTYPE + DATA
        if (len > 0xFF)
            throw new ArgumentOutOfRangeException(nameof(data), $"负载过长：LEN={len} 超出单字节");

        var frame = new byte[HeaderLength + len];
        frame[0] = Head0;
        frame[1] = Head1;
        frame[2] = Head2;
        frame[3] = (byte)len;
        frame[4] = baseCmd;
        frame[5] = BaseId;
        frame[6] = cmdType;
        data.CopyTo(frame.AsSpan(7));
        return frame;
    }

    /// <summary>
    /// 解析一帧**逻辑帧**（调用方应确保已去补零/已定帧）。
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> logicalFrame, out byte baseCmd, out byte cmdType, out byte[] data)
    {
        baseCmd = 0;
        cmdType = 0;
        data = Array.Empty<byte>();

        if (logicalFrame.Length < MinTotalLength)
            return false;
        if (logicalFrame[0] != Head0 || logicalFrame[1] != Head1 || logicalFrame[2] != Head2)
            return false;

        int len = logicalFrame[3];
        if (len < 4)
            return false;
        if (logicalFrame.Length < HeaderLength + len)
            return false;
        if (logicalFrame[5] != BaseId)
            return false;

        baseCmd = logicalFrame[4];
        cmdType = logicalFrame[6];
        data = logicalFrame.Slice(7, len - 3).ToArray();
        return true;
    }

    /// <summary>
    /// 从**定长载体**（USB 64B 报告，尾部补零）中按 `LEN` 截出逻辑帧。
    /// 串口形态不使用本方法（其定帧由 <see cref="CompanyFrameReader"/> 完成）。
    /// </summary>
    public static bool TryTrimToLogical(ReadOnlySpan<byte> raw, out byte[] logical)
    {
        logical = Array.Empty<byte>();

        if (raw.Length < MinTotalLength)
            return false;
        if (raw[0] != Head0 || raw[1] != Head1 || raw[2] != Head2)
            return false;

        int len = raw[3];
        if (len < 4)
            return false;

        int total = HeaderLength + len;
        if (total > raw.Length)
            return false;

        logical = raw[..total].ToArray();
        return true;
    }
}
