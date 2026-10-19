using Microsoft.Extensions.Logging.Abstractions;
using PencilBox.Core.Features;
using PencilBox.Infrastructure.Config;
using Xunit;

namespace PencilBox.Tests;

/// <summary>
/// 功能模块总开关（14 号架构提案，2026-10-19）：
/// 目录完整性 / 默认全开（老用户零行为变化）/ 开关持久化回环 / 损坏回落 / 未知 id 拼写守卫。
/// </summary>
public class FeatureToggleTests : IDisposable
{
    private readonly string _dir;

    public FeatureToggleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pencilbox-feattest-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private FeatureToggleService Create() => new(NullLogger<FeatureToggleService>.Instance, baseDirectory: _dir);

    private string JsonPath => Path.Combine(_dir, "configs", "features.json");

    // ===== 目录 =====

    [Fact]
    public void Catalog_IdsUnique_AllOptionalByDefault()
    {
        var ids = FeatureCatalog.All.Select(f => f.Id).ToList();

        Assert.True(ids.Count >= 3, "首批至少 airmouse/audio/seriallog 三项");
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(FeatureCatalog.All, f =>
        {
            Assert.True(f.DefaultEnabled, $"{f.Id} 默认应全开（老用户零行为变化）");
            Assert.False(string.IsNullOrWhiteSpace(f.Name));
            Assert.False(string.IsNullOrWhiteSpace(f.Description));
            Assert.True(f.CliDomains.Count > 0, $"{f.Id} 应声明管辖 CLI 域");
        });

        // 核心域不进目录（指挥官 10-19 二轮裁决：设备强相关功能跟连接同生共死，不可关）
        Assert.DoesNotContain(ids, id => id is "keymap" or "ota" or "device");
    }

    // ===== 默认与回环 =====

    [Fact]
    public void NoFile_AllEnabledByDefault()
    {
        var svc = Create();

        Assert.All(svc.Describe(), f => Assert.True(svc.IsEnabled(f.Id)));
        Assert.False(File.Exists(JsonPath), "未动开关不应创建配置文件");
    }

    [Fact]
    public async Task ExternalFileChange_FiresChanged_CrossProcessFollow()
    {
        // 跨进程跟随（10-19 E2E 实测缺口）：另一进程直写 features.json → FSW 防抖重读 → Changed 事件
        var svc = Create();
        var tcs = new TaskCompletionSource<FeatureToggleChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.Changed += (_, e) => tcs.TrySetResult(e);

        File.WriteAllText(JsonPath, """{"features": {"audio": false}}""");

        var winner = await Task.WhenAny(tcs.Task, Task.Delay(4000));
        Assert.True(winner == tcs.Task, "4s 内未收到外部文件变更的 Changed 事件");
        Assert.Equal("audio", tcs.Task.Result.Descriptor.Id);
        Assert.False(tcs.Task.Result.Enabled);
        Assert.False(svc.IsEnabled("audio"));
    }

    [Fact]
    public void SetEnabled_PersistsAndRoundTripsAcrossInstances()
    {
        var svc = Create();
        svc.SetEnabled("airmouse", enabled: false);

        Assert.True(File.Exists(JsonPath));

        // 新实例（≈新进程/新会话）：GUI 关掉的开关 CLI 侧同样生效——同一份文件
        var second = Create();
        Assert.False(second.IsEnabled("airmouse"));
        Assert.True(second.IsEnabled("audio"), "未动功能不受影响");
        Assert.True(second.IsEnabled("AIRMOUSE") == false, "id 比对大小写不敏感");
    }

    [Fact]
    public void SetEnabled_RaisesChangedEvent()
    {
        var svc = Create();
        FeatureToggleChangedEventArgs? received = null;
        svc.Changed += (_, e) => received = e;

        svc.SetEnabled("audio", false);

        Assert.NotNull(received);
        Assert.Equal("audio", received!.Descriptor.Id);
        Assert.False(received.Enabled);
    }

    // ===== 损坏与未知键 =====

    [Fact]
    public void CorruptJson_FallsBackToDefaults_DoesNotThrow()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(JsonPath)!);
        File.WriteAllText(JsonPath, "{ this is not json !!!");

        var svc = Create();

        Assert.All(svc.Describe(), f => Assert.True(svc.IsEnabled(f.Id)));
    }

    [Fact]
    public void UnknownKeysInFile_AreIgnored()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(JsonPath)!);
        File.WriteAllText(JsonPath,
            """{ "features": { "airmouse": false, "legacy-gone-feature": true } }""");

        var svc = Create();

        Assert.False(svc.IsEnabled("airmouse"));
    }

    // ===== 拼写守卫 =====

    [Fact]
    public void UnknownFeatureId_ThrowsKeyNotFound()
    {
        var svc = Create();

        Assert.Throws<KeyNotFoundException>(() => svc.IsEnabled("airmose"));
        Assert.Throws<KeyNotFoundException>(() => svc.SetEnabled("nope", false));
        Assert.Throws<KeyNotFoundException>(() => svc.GetDescriptor("nope"));
    }
}
