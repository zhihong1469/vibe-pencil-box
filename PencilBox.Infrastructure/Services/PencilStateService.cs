using Microsoft.Extensions.Logging;
using PencilBox.Core.Models;
using PencilBox.Core.Protocol;
using PencilBox.Core.Services;

namespace PencilBox.Infrastructure.Services;

/// <summary>
/// 笔端状态查询服务实现
/// 读取电量、蓝牙连接状态、信号强度、设备信息、OTA 支持。
/// 对齐 link/host/usb/03_笔端状态查询.md
/// </summary>
public class PencilStateService : IPencilStateService
{
    private readonly ICommandClient _commandClient;
    private readonly ILogger<PencilStateService> _logger;

    public PencilState? CurrentState { get; private set; }

    public event EventHandler<PencilState>? StateUpdated;

    public PencilStateService(ICommandClient commandClient, ILogger<PencilStateService> logger)
    {
        _commandClient = commandClient;
        _logger = logger;
    }

    public async Task<PencilState?> GetPencilStateAsync(CancellationToken ct = default)
    {
        if (!_commandClient.IsConnected)
            return CurrentState;

        var payload = await _commandClient.SendCustomAsync(
            PcSubCommand.GetPencilState, null, 2000, ct);

        if (payload == null || payload.Length < 5)
            return CurrentState;

        var state = PencilState.FromBytes(payload);
        CurrentState = state;
        StateUpdated?.Invoke(this, state);
        return state;
    }

    public async Task<byte?> QueryBatteryAsync(CancellationToken ct = default)
    {
        var payload = await _commandClient.SendCustomAsync(
            PcSubCommand.QueryBattery, null, 5000, ct);

        // 应答：payload = [status][电量%]
        if (payload == null || payload.Length < 2 || payload[0] != 0x00)
            return null;

        byte battery = payload[1];
        return battery;
    }

    public async Task<PencilDeviceInfo?> QueryDeviceInfoAsync(CancellationToken ct = default)
    {
        var payload = await _commandClient.SendCustomAsync(
            PcSubCommand.QueryDeviceInfo, null, 5000, ct);

        if (payload == null || payload.Length < 8)
            return null;

        return PencilDeviceInfo.FromBytes(payload);
    }

    public async Task<OtaSupportInfo?> GetOtaSupportAsync(CancellationToken ct = default)
    {
        var payload = await _commandClient.SendCustomAsync(
            PcSubCommand.GetOtaSupport, null, 2000, ct);

        if (payload == null || payload.Length < 3)
            return null;

        return OtaSupportInfo.FromBytes(payload);
    }
}
