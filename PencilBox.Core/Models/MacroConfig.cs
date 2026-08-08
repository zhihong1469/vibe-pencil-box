namespace PencilBox.Core.Models;

/// <summary>
/// 按键宏配置
/// </summary>
public class MacroConfig
{
    /// <summary>
    /// 宏配置 ID
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// 宏名称
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 描述
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 按键组合列表
    /// </summary>
    public List<KeyAction> KeySequence { get; set; } = new();

    /// <summary>
    /// 是否循环播放
    /// </summary>
    public bool Loop { get; set; }

    /// <summary>
    /// 循环次数（0 = 无限循环）
    /// </summary>
    public int LoopCount { get; set; }

    /// <summary>
    /// 按键之间的间隔（毫秒）
    /// </summary>
    public int KeyIntervalMs { get; set; } = 100;

    /// <summary>
    /// 是否启用
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 更新时间
    /// </summary>
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// 按键动作
/// </summary>
public class KeyAction
{
    /// <summary>
    /// 按键类型
    /// </summary>
    public KeyType Type { get; set; }

    /// <summary>
    /// 键码（标准 HID 键盘扫描码或 Consumer 码）
    /// </summary>
    public ushort KeyCode { get; set; }

    /// <summary>
    /// 按下持续时间（毫秒）
    /// </summary>
    public int DurationMs { get; set; } = 100;

    /// <summary>
    /// 修饰键组合
    /// </summary>
    public ModifierKey Modifiers { get; set; }

    /// <summary>
    /// 延迟（毫秒，在执行此动作前的等待时间）
    /// </summary>
    public int DelayMs { get; set; }
}

/// <summary>
/// 按键类型
/// </summary>
public enum KeyType
{
    /// <summary>
    /// 标准键盘按键（A-Z, 0-9, F1-F12 等）
    /// </summary>
    Keyboard,

    /// <summary>
    /// Consumer 控制（播放/暂停/静音/音量等）
    /// </summary>
    Consumer,

    /// <summary>
    /// 鼠标按键
    /// </summary>
    MouseButton,

    /// <summary>
    /// 鼠标移动
    /// </summary>
    MouseMove,

    /// <summary>
    /// 延时（不执行任何操作，仅等待）
    /// </summary>
    Delay
}

/// <summary>
/// 修饰键组合（位掩码）
/// </summary>
[Flags]
public enum ModifierKey
{
    /// <summary>
    /// 无修饰键
    /// </summary>
    None = 0,

    /// <summary>
    /// Left Control
    /// </summary>
    LeftCtrl = 0x01,

    /// <summary>
    /// Left Shift
    /// </summary>
    LeftShift = 0x02,

    /// <summary>
    /// Left Alt
    /// </summary>
    LeftAlt = 0x04,

    /// <summary>
    /// Left GUI (Windows Key)
    /// </summary>
    LeftGui = 0x08,

    /// <summary>
    /// Right Control
    /// </summary>
    RightCtrl = 0x10,

    /// <summary>
    /// Right Shift
    /// </summary>
    RightShift = 0x20,

    /// <summary>
    /// Right Alt
    /// </summary>
    RightAlt = 0x40,

    /// <summary>
    /// Right GUI
    /// </summary>
    RightGui = 0x80
}
