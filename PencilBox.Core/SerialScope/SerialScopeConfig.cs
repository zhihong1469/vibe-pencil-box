namespace PencilBox.Core.SerialScope;

/// <summary>
/// 串口抓日志的**预置配置**（对应 `config/serial.json`；05 §3.4）。
/// `pencilbox serial log --all` 时按此起多个端口，免得每次手打一串参数。
/// </summary>
public sealed class SerialScopeConfig
{
    /// <summary>要同时抓的端口（双口可选可同时，05 §3.5）</summary>
    public List<SerialScopePortSpec> Ports { get; set; } = new();

    /// <summary>日志目录（留空 ⇒ 用默认目录：`%LocalAppData%/PencilBox/logs/serial`）</summary>
    public string? LogDir { get; set; }

    /// <summary>轮转策略（大小上限 / 保留份数）</summary>
    public SerialScopeRotationConfig Rotation { get; set; } = new();

    /// <summary>是否同时写 JSONL（机读流）</summary>
    public bool Jsonl { get; set; } = true;

    /// <summary>是否把 F5 帧解码成人类可读（关=原样 HEX）</summary>
    public bool DecodeFrames { get; set; } = true;

    /// <summary>启动时是否清空原日志（05 §3.5：默认清空并显著提示）</summary>
    public bool ClearOnStart { get; set; } = true;
}

/// <summary>配置里的一个端口</summary>
public sealed class SerialScopePortSpec
{
    /// <summary>端口标签（写进每行 `[NAME]`，如 `PENCIL` / `DONGLE`）</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>串口号；`auto` = 自动挑一个未被占用的（避开调试器虚拟口）</summary>
    public string Port { get; set; } = "auto";

    /// <summary>波特率（我们设备用 **1M**，故默认 1000000）</summary>
    public int BaudRate { get; set; } = 1_000_000;
}

/// <summary>配置里的轮转参数（字节数，非 `10M` 文本形式）</summary>
public sealed class SerialScopeRotationConfig
{
    /// <summary>单文件上限（默认 10MB）</summary>
    public long MaxBytes { get; set; } = LogRotationPolicy.DefaultMaxBytes;

    /// <summary>保留文件总份数（默认 10）</summary>
    public int BackupCount { get; set; } = LogRotationPolicy.DefaultBackupCount;

    /// <summary>折算成策略对象</summary>
    public LogRotationPolicy ToPolicy() => new(MaxBytes, BackupCount);
}
