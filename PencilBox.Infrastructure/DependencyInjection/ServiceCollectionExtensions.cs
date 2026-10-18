// <copyright file="ServiceCollectionExtensions.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PencilBox.Core.Communication;
using PencilBox.Core.Configuration;
using PencilBox.Core.Fsm;
using PencilBox.Core.Protocol.Company;
using PencilBox.Core.Services;
using PencilBox.Infrastructure.Audio;
using PencilBox.Infrastructure.Ble;
using PencilBox.Infrastructure.Config;
using PencilBox.Infrastructure.Dsp;
using PencilBox.Infrastructure.Hid;
using PencilBox.Infrastructure.Platform;
using PencilBox.Infrastructure.Serial;
using PencilBox.Infrastructure.Services;

namespace PencilBox.Infrastructure.DependencyInjection;

/// <summary>
/// Infrastructure layer options for DI configuration
/// </summary>
public class InfrastructureOptions
{
    /// <summary>
    /// Python executable path for DSP (empty = auto-detect)
    /// </summary>
    public string PythonPath { get; set; } = string.Empty;

    /// <summary>
    /// DSP scripts directory path
    /// </summary>
    public string DspScriptsPath { get; set; } = string.Empty;

    /// <summary>
    /// DSP execution timeout in milliseconds
    /// </summary>
    public int DspTimeoutMs { get; set; } = 30000;
}

/// <summary>
/// Shared DI registration for PencilBox infrastructure services.
/// Used by CLI, MCP, and Desktop alike.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register all PencilBox infrastructure services
    /// </summary>
    public static IServiceCollection AddPencilBoxInfrastructure(
        this IServiceCollection services,
        Action<InfrastructureOptions>? configure = null)
    {
        var options = new InfrastructureOptions();
        configure?.Invoke(options);

        // Transport layer
        services.AddSingleton<IHidTransport, HidSharpTransport>();

        // ===== 串口共享网关（06 M1：一个进程一个物理口一个句柄）=====
        // 每口一个 IO 实例（包 SerialTransport：正式路径 + UMDF 裸句柄兜底 + DTR/RTS 纪律全在里面）
        services.AddSingleton<ISerialPortHub>(sp => new Core.Communication.SerialPortHub(
            () => new SerialPortIo(sp.GetRequiredService<ILoggerFactory>())));

        // 命令面（读 MAC 0x51 / OTA 串口轨 0x16 / CLI company）走 hub 共享口；
        // 内嵌一个仅用于枚举的 SerialTransport（自身永不连接）
        services.AddSingleton<ISerialTransport>(sp => new SharedSerialTransport(
            sp.GetRequiredService<ISerialPortHub>(),
            new SerialTransport(sp.GetRequiredService<ILoggerFactory>().CreateLogger<SerialTransport>()),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<SharedSerialTransport>()));

        // BLE 广播扫描（Windows WinRT 实现；识别/推荐在 Core）
        services.AddSingleton<IBleScanner, BleScanner>();

        // BLE 中心设备（PC"类基站"测试位：GAP 建连/GATT 枚举读写订阅/SMP 配对，压测编排 BleStressRunner 在 Core）
        services.AddSingleton<IBleCentral, WinRtBleCentral>();

        // Audio capture
        services.AddSingleton<IAudioCapture, NAudioAudioCapture>();

        // 扩音直通（采集→扬声器实时回路；与录音路径独立）
        services.AddSingleton<IAudioPassthrough, WasapiPassthroughEngine>();

        // Business services
        // DeviceService 同时实现 IDeviceService + ICommandClient（共享同一实例）
        services.AddSingleton<DeviceService>();
        services.AddSingleton<IDeviceService>(sp => sp.GetRequiredService<DeviceService>());
        services.AddSingleton<ICommandClient>(sp => sp.GetRequiredService<DeviceService>());
        services.AddSingleton<IMacroService, MacroService>();
        services.AddSingleton<IAudioService, AudioService>();
        // 命名 Keymap 配置存储（pc-issues/04 提案 B：exe 同级 configs/keymap/，默认目录参数走可选构造注入）
        services.AddSingleton<IKeymapConfigStore, KeymapConfigStore>();
        services.AddSingleton<IPencilStateService, PencilStateService>();

        // 笔端绑定服务（取 MAC + 目录 + 下发 门面）：两条来源（BLE 扫描 / 串口 0x51 读）汇入同一份目录
        services.AddSingleton<IPenBindingService, PenBindingService>();

        // `0x61` 内部调参服务（issues-pencil/01）：仅串口轨 PC↔笔端直连（空鼠页用；CLI 走 codec 直调）
        services.AddSingleton<ITuningService, TuningService>();
        // 空鼠方案快照存储（03 卷 §6：configs/airmouse/ 一快照一 JSON，CLI 与空鼠工作台共用）
        services.AddSingleton<IAirMouseSnapshotStore, AirMouseSnapshotStore>();

        // EP4 命令通道客户端（A1~A4 一问一答）：UI/CLI 经此读 Dongle 真实应答
        services.AddSingleton<IDongleCommandClient, DongleCommandClient>();

        // ===== 公司 V0.9 协议接入（03/04：协议约定 / 发送通道 / 会话 FSM 三层）=====
        // 三层单向依赖：FSM → 接口（Codec/Channel/Clock），Codec 与 Channel 互不依赖。
        services.AddSingleton<CompanyConfig>();                 // 未定值集中一处，便于单点切换
        services.AddSingleton<IClock, SystemClock>();           // 时间抽象（FSM 超时/节拍可控可测）
        services.AddSingleton<ICompanyCodec, CompanyCodec>();   // 纯函数：0x03/0x51/0x52 载荷 + F5 帧底座
        // 双通道并存（R3）：USB（基站）+ 串口（笔端）各一实例，由 FSM 按会话路由；二者只认"逻辑帧"
        services.AddSingleton<HidCommandChannel>();
        services.AddSingleton<SerialCommandChannel>();
        // `0x53` 状态上行会话服务（协议补充 05）：拉取（基站缓存即答）+ 推送监听；仅 USB 基站轨。
        // 服务只依赖 ICommandChannel 接口（分层铁律）；此处显式绑定 USB 基站通道实现
        services.AddSingleton<CompanyPenStatusService>(sp => new CompanyPenStatusService(
            sp.GetRequiredService<ICompanyCodec>(),
            sp.GetRequiredService<HidCommandChannel>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<CompanyConfig>()));

        // `0x54` 信封会话服务（08）：显式绑定 USB 基站通道 = **BLE 中转形态**（UI 卡片用）；
        // C5 让路接进程级 OTA 会话闸（OTA 页起会话置位、终态复位）
        services.AddSingleton<CompanyPenCmdService>(sp => new CompanyPenCmdService(
            sp.GetRequiredService<ICompanyCodec>(),
            sp.GetRequiredService<HidCommandChannel>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<CompanyConfig>(),
            () => OtaSessionActivity.InProgress));
        services.AddSingleton<IVpCommandService, F5EnvelopeVpCommand>();

        // `0x55` 快捷键可配置会话服务（pc-issues/05；"生效"按钮的下发腿）：显式绑定 USB 基站通道（同 0x53/0x54 形态）
        services.AddSingleton<CompanyKeymapConfigService>(sp => new CompanyKeymapConfigService(
            sp.GetRequiredService<HidCommandChannel>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<CompanyConfig>()));
        // 全局键盘钩子（笔端工作状态自管的观测腿；被动监听不吞键，钩子线程内回调）
        services.AddSingleton<IGlobalKeyboardHook, GlobalKeyboardHook>();

        // 串口抓日志（05 §3.4 S2/S3；06 M1 升级：经 hub——口被命令面占用时自动旁路共享，独占开口也注册进 hub 供命令面复用）
        // 单例只持有"跑一次会话"的能力，真正的读写器在 RunAsync 内按需创建并收尾
        services.AddSingleton<SerialLogSession>();

        // 对拍摘要（05 §4 S4）：CLI `serial diff` 与 UI 串口页"对拍"共用
        services.AddSingleton<SerialScopeDiffService>();

        // 已知笔端目录持久化（扫到即保存，设备暂不在场也能据此下发）
        services.AddSingleton<IPenCatalogStore, JsonPenCatalogStore>();

        // 协议域「绑定目标下发」：公司 V0.9 F5 —— 0x51 写配对信息（显示序零转换）+ 0x52 进配对
        services.AddSingleton<ITargetBindSender, CompanyTargetBindSender>();

        // Configuration storage
        services.AddSingleton<IConfigStore, JsonConfigStore>();

        // DSP service
        services.AddSingleton<IDspService>(sp =>
        {
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PythonProcessBridge>>();
            return new PythonProcessBridge(
                logger,
                string.IsNullOrEmpty(options.PythonPath) ? null : options.PythonPath,
                string.IsNullOrEmpty(options.DspScriptsPath) ? null : options.DspScriptsPath,
                options.DspTimeoutMs);
        });

        return services;
    }
}
