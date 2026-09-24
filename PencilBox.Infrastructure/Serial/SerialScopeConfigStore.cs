using System.Text.Json;
using PencilBox.Core.SerialScope;

namespace PencilBox.Infrastructure.Serial;

/// <summary>
/// `config/serial.json` 的读写（05 §3.4：「配置：端口、名称、波特率、轮转、清空策略、解码开关」）。
///
/// 只在 `serial log --all` 时用：把"双口同时"预置成一次点击，免得每次手打一串参数。
/// 读取**大小写不敏感**——参考实现 `serial-chat/config.json` 用的是 `baudrate`（全小写），
/// 两种写法都认，便于直接从那边抄配置过来。
/// </summary>
public static class SerialScopeConfigStore
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>读取配置；不存在或解析失败返回 <c>null</c> 并给出原因</summary>
    public static SerialScopeConfig? Load(string path, out string? error)
    {
        error = null;

        if (!File.Exists(path))
        {
            error = $"配置文件不存在：{path}";
            return null;
        }

        try
        {
            var config = JsonSerializer.Deserialize<SerialScopeConfig>(File.ReadAllText(path), ReadOptions);
            if (config == null)
            {
                error = $"配置文件为空：{path}";
                return null;
            }
            if (config.Ports.Count == 0)
            {
                error = $"配置文件里没有 ports：{path}";
                return null;
            }
            return config;
        }
        catch (JsonException ex)
        {
            error = $"配置文件解析失败：{path}（{ex.Message}）";
            return null;
        }
    }

    /// <summary>写一份样例配置（首次用 `--all` 时生成，供按需修改）</summary>
    public static void WriteSample(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, SampleJson(), new System.Text.UTF8Encoding(false));
    }

    /// <summary>
    /// 样例配置文本。**手写而非序列化**：配置是要给人改的，带 `//` 说明比一堆 null 有用得多
    /// （读取侧已开 `JsonCommentHandling.Skip`，注释与尾逗号都认）。
    /// 样例刻意用 `auto`：不写死别人机器上的 COM 号。
    /// </summary>
    public static string SampleJson() =>
        """
        {
          // 要同时抓的端口（双口可选可同时）。port 写 "auto" = 自动挑一个未被占用的口
          //（按类型猜，可能猜错；要对拍请在跑之前写死成实际 COM 号）
          "ports": [
            { "name": "PENCIL", "port": "auto", "baudRate": 1000000 },
            { "name": "DONGLE", "port": "auto", "baudRate": 1000000 }
          ],

          // 日志目录；留空 = %LocalAppData%\PencilBox\logs\serial
          "logDir": "",

          // 轮转：单文件上限 10MB，保留 10 份（当前 + 9 份备份）
          "rotation": { "maxBytes": 10485760, "backupCount": 10 },

          "jsonl": true,          // 是否同时写机读 JSONL
          "decodeFrames": true,   // 是否把 F5 帧解码成人类可读（false = 原样 HEX）
          "clearOnStart": true    // 启动是否清空原日志（false = 追加）
        }

        """;
}
