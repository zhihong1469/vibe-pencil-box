// <copyright file="OtaModels.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Models;

/// <summary>
/// OTA 包结构（构建后可在传输层直接使用）
/// </summary>
public class OtaPackage
{
    /// <summary>
    /// 产品 ID（用于绑定到目标硬件型号）
    /// </summary>
    public uint ProductId { get; set; }

    /// <summary>
    /// 目标固件版本（0xMmmpp 格式，如 0x020001 = v2.0.1）
    /// </summary>
    public uint TargetVersion { get; set; }

    /// <summary>
    /// 原始固件字节大小（未加密）
    /// </summary>
    public int OriginalSize { get; set; }

    /// <summary>
    /// Command Object（含签名和 IV），最大 256 字节
    /// </summary>
    public byte[] CommandObject { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Data Object（已加密的固件数据，或明文如果未启用加密）
    /// </summary>
    public byte[] DataObject { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Data Object 的 CRC32（用于 PRN 流控校验）
    /// </summary>
    public uint Crc32 { get; set; }

    /// <summary>
    /// PRN（Packet Receive Notification）每 N 个包等待一次 CRC 通知
    /// </summary>
    public ushort Prn { get; set; } = 8;

    /// <summary>
    /// 是否已加密（AES-128-CTR）
    /// </summary>
    public bool IsEncrypted { get; set; }

    /// <summary>
    /// 是否已签名（ECDSA-SHA256 / secp256r1）
    /// </summary>
    public bool IsSigned { get; set; }

    /// <summary>
    /// AES IV（16 字节，每次 OTA 随机生成）
    /// </summary>
    public byte[] AesIv { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 文件名（可选，便于管理多个 OTA 包）
    /// </summary>
    public string? SourceFileName { get; set; }
}

/// <summary>
/// OTA 构建选项
/// </summary>
public record OtaBuildOptions
{
    /// <summary>
    /// 原始固件（BIN 文件）
    /// </summary>
    public byte[] Firmware { get; init; } = Array.Empty<byte>();

    /// <summary>
    /// AES-128 密钥（16 字节）
    /// </summary>
    public byte[]? AesKey { get; init; }

    /// <summary>
    /// ECDSA 私钥（PEM 文件路径）
    /// </summary>
    public string? PrivateKeyPath { get; init; }

    /// <summary>
    /// 产品 ID
    /// </summary>
    public uint ProductId { get; init; }

    /// <summary>
    /// 目标版本
    /// </summary>
    public uint TargetVersion { get; init; }

    /// <summary>
    /// 是否启用加密
    /// </summary>
    public bool Encrypt { get; init; } = true;

    /// <summary>
    /// 是否启用签名
    /// </summary>
    public bool Sign { get; init; } = true;

    /// <summary>
    /// PRN 值（默认 8）
    /// </summary>
    public ushort Prn { get; init; } = 8;
}

/// <summary>
/// OTA 升级状态
/// </summary>
public enum OtaState
{
    Idle,
    Connecting,
    SendingCommand,
    SendingData,
    Executing,
    WaitingReboot,
    Completed,
    Cancelled,
    Error
}

/// <summary>
/// OTA 状态变化事件参数
/// </summary>
public class OtaStateChangedEventArgs : EventArgs
{
    public OtaState State { get; }
    public OtaState PreviousState { get; }
    public string? Message { get; }
    public Exception? Exception { get; }

    public OtaStateChangedEventArgs(OtaState state, OtaState previousState,
        string? message = null, Exception? exception = null)
    {
        State = state;
        PreviousState = previousState;
        Message = message;
        Exception = exception;
    }
}

/// <summary>
/// OTA 进度
/// </summary>
public record OtaProgress(
    double Percent,
    long BytesSent,
    long TotalBytes,
    double SpeedKBps,
    TimeSpan Elapsed,
    TimeSpan? EstimatedRemaining,
    string CurrentPhase);

/// <summary>
/// OTA 进度事件参数
/// </summary>
public class OtaProgressEventArgs : EventArgs
{
    public OtaProgress Progress { get; }
    public OtaProgressEventArgs(OtaProgress progress) => Progress = progress;
}

/// <summary>
/// OTA 升级结果
/// </summary>
public record OtaResult(
    bool Success,
    string? ErrorMessage,
    OtaState FinalState,
    TimeSpan TotalTime,
    long BytesTransferred)
{
    public static OtaResult Ok(OtaState finalState, TimeSpan time, long bytes)
        => new(true, null, finalState, time, bytes);

    public static OtaResult Fail(string error, OtaState finalState = OtaState.Error,
        TimeSpan time = default, long bytes = 0)
        => new(false, error, finalState, time, bytes);

    public static OtaResult Cancelled(TimeSpan time = default)
        => new(false, "User cancelled", OtaState.Cancelled, time, 0);
}

/// <summary>
/// OTA 命令码（PC ↔ Dongle）
/// </summary>
public static class OtaCommand
{
    // PC → Dongle
    public const byte Begin = 0x01;
    public const byte CmdObject = 0x02;
    public const byte DataObject = 0x03;
    public const byte Execute = 0x04;
    public const byte Cancel = 0x05;
    public const byte QueryStatus = 0x06;
    public const byte SetPrn = 0x07;
    public const byte Select = 0x08;  // 查询续传点

    // Dongle → PC
    public const byte Ready = 0x80;
    public const byte Rsp = 0x81;
    public const byte Progress = 0x82;
    public const byte PrnNotify = 0x83;
    public const byte Error = 0x84;
    public const byte Disconnected = 0x85;
}

/// <summary>
/// OTA 错误码
/// </summary>
public static class OtaErrorCode
{
    public const byte BleConnectFailed = 0x01;
    public const byte DfuServiceNotFound = 0x02;
    public const byte DfuResponseFailed = 0x03;
    public const byte CrcMismatch = 0x04;
    public const byte SignatureInvalid = 0x05;
    public const byte VersionNotMatch = 0x06;
    public const byte FlashWriteFailed = 0x07;
    public const byte Timeout = 0x08;
    public const byte Cancelled = 0x09;
    public const byte Unknown = 0xFF;
}