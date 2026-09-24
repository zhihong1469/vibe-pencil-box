using Microsoft.Extensions.Logging;
using PencilBox.Core.Ble;
using PencilBox.Core.Communication;
using PencilBox.Core.Configuration;
using PencilBox.Core.Fsm;
using PencilBox.Core.Protocol.Company;
using PencilBox.Core.Services;
using PencilBox.Infrastructure.Serial;

namespace PencilBox.Infrastructure.Services;

/// <summary>
/// 笔端绑定服务（取 MAC + 目录 + 下发 门面实现）。
///
/// **两条来源、一份存储**：
/// - <see cref="ScanPensAsync"/>：BLE 广播扫描（广播地址为权威 MAC；名字尾号仅展示/校验）；
/// - <see cref="ScanPenViaSerialAsync"/>：串口直连笔端发 `0x51` 读（笔端恒回自身 MAC + `BT_NAME`）。
/// 两者都经 <see cref="KnownPenCatalog.Upsert"/> / <see cref="KnownPenCatalog.Merge"/> 汇入
/// 同一份 <see cref="IPenCatalogStore"/> 目录（显示序 MAC 单源），因此下发路径与来源无关。
///
/// 解耦约定：本类只做"取 MAC + 目录"；帧封装/发送/回读校验由协议域
/// <see cref="ITargetBindSender"/>（<c>0x51</c>/<c>0x52</c>）负责。
/// </summary>
public class PenBindingService : IPenBindingService
{
    private readonly IBleScanner _bleScanner;
    private readonly IPenCatalogStore _catalog;
    private readonly ITargetBindSender _targetBindSender;
    private readonly ICompanyCodec _codec;
    private readonly ISerialTransport _serialTransport;
    private readonly SerialCommandChannel _serialChannel;
    private readonly CommandRequester _requester;
    private readonly CompanyConfig _config;
    private readonly ILogger<PenBindingService> _logger;

    public PenBindingService(IBleScanner bleScanner, IPenCatalogStore catalog,
        ITargetBindSender targetBindSender, ICompanyCodec codec,
        ISerialTransport serialTransport, SerialCommandChannel serialChannel,
        IClock clock, CompanyConfig config, ILogger<PenBindingService> logger)
    {
        _bleScanner = bleScanner;
        _catalog = catalog;
        _targetBindSender = targetBindSender;
        _codec = codec;
        _serialTransport = serialTransport;
        _serialChannel = serialChannel;
        _requester = new CommandRequester(clock, config);
        _config = config;
        _logger = logger;
    }

    public async Task<IReadOnlyList<KnownPen>> LoadKnownPensAsync(CancellationToken cancellationToken = default)
    {
        var existing = await _catalog.LoadAllAsync(cancellationToken);
        return KnownPenCatalog.AssignLevels(existing);
    }

    // ============ 来源①：BLE 广播扫描 ============

    public async Task<IReadOnlyList<KnownPen>> ScanPensAsync(int durationSec = 5,
        CancellationToken cancellationToken = default)
    {
        var existing = (await _catalog.LoadAllAsync(cancellationToken)).ToList();
        var scans = await _bleScanner.ScanAsync(durationSec, cancellationToken);
        var scansList = scans.Where(s => s.IsSunVotePen).ToList();

        // 名字尾号只作展示/校验：权威 MAC 取广播地址；不一致仅告警（不阻塞）
        foreach (var s in scansList)
        {
            if (BleNaming.IsMacSuffixConsistent(s.LocalName, s.MacDisplayOrder) == false)
            {
                _logger.LogWarning(
                    "广播名尾号与广播地址不一致（以地址为准）：name=\"{Name}\" addr={Addr}",
                    s.LocalName, s.MacDisplayText);
            }
        }

        // 扫描前先清在场标记（本次不在场的视为离线，但保留在目录）
        foreach (var p in existing)
            p.IsPresent = false;

        var merged = KnownPenCatalog.Merge(existing, scansList);
        await _catalog.SaveAllAsync(merged, cancellationToken);

        _logger.LogInformation("Pen scan -> catalog {Count} known (scanned {N})", merged.Count, scansList.Count);
        return KnownPenCatalog.AssignLevels(merged);
    }

    // ============ 来源②：串口直连笔端读 MAC（公司协议 0x51） ============

    public async Task<IReadOnlyList<KnownPen>> ScanPenViaSerialAsync(string portName,
        CancellationToken cancellationToken = default)
    {
        await EnsureSerialOpenAsync(portName, cancellationToken);

        var request = _codec.EncodePairRead(0x00);   // 笔端不看 NO，恒回自身
        _logger.LogInformation("0x51 读笔端配对信息（串口 {Port}）", portName);

        var resp = await _requester.RequestAsync(_serialChannel, request, CompanyCmd.PairInfo, cancellationToken)
            .ConfigureAwait(false);

        if (resp == null)
            throw new InvalidOperationException(
                $"0x51 读无应答（{portName} 超时）：请确认串口接线（TX/RX 交叉、共地）与笔端固件已就绪");

        if (!_codec.TryParsePairReadResp(resp, out var status, out var info) || info == null)
            throw new InvalidOperationException(
                $"0x51 读笔端失败：STATUS=0x{status:X2}（应答未携带有效 MAC）");

        var pen = new KnownPen
        {
            MacDisplayHex = Convert.ToHexString(info.MacDisplay6),   // 显示序单源
            Source = PenMacSource.SerialRead,
            BtName = info.BtName,
            LocalName = info.BtName,                                 // 展示用：与广播名同前缀口径
            Rssi = 0,                                                // 串口来源无信号强度
            IsPresent = false                                        // "在场"由 BLE 扫描判定，串口读不改变该语义
        };

        var existing = (await _catalog.LoadAllAsync(cancellationToken)).ToList();
        var merged = KnownPenCatalog.Upsert(existing, pen);
        await _catalog.SaveAllAsync(merged, cancellationToken);

        _logger.LogInformation("Serial read pen {Mac} -> catalog {Count} known",
            pen.MacDisplayText, merged.Count);

        return KnownPenCatalog.AssignLevels(merged);
    }

    /// <summary>清空本地已知笔端目录（仅 PC 记录；基站已下发绑定不受影响）</summary>
    public async Task ClearKnownPensAsync(CancellationToken cancellationToken = default)
    {
        await _catalog.ClearAllAsync(cancellationToken);
        _logger.LogInformation("Known pens catalog cleared by user");
    }

    /// <summary>确保目标串口已打开（端口不同则重连）；`115200,N,8,1` 且**不施加 DTR/RTS**（避免复位笔端）</summary>
    private async Task EnsureSerialOpenAsync(string portName, CancellationToken cancellationToken)
    {
        if (_serialTransport.IsConnected &&
            string.Equals(_serialTransport.CurrentPortName, portName, StringComparison.OrdinalIgnoreCase))
            return;

        var settings = new SerialPortSettings { BaudRate = _config.SerialBaudRate };
        if (!await _serialTransport.ConnectAsync(portName, settings, cancellationToken))
            throw new InvalidOperationException($"打开串口失败：{portName}（可能被占用或端口号有误）");
    }

    // ============ 下发给基站（统一路径，与来源无关） ============

    public async Task<BindingSendResult> BindPenByMacAsync(KnownPen pen,
        CancellationToken cancellationToken = default)
    {
        // 标记"使用过/上次使用"并持久化
        pen.UseCount += 1;
        pen.LastUsedUtc = DateTime.UtcNow;

        var catalog = (await _catalog.LoadAllAsync(cancellationToken)).ToList();
        var idx = catalog.FindIndex(p =>
            string.Equals(p.MacDisplayHex, pen.MacDisplayHex, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            catalog[idx].UseCount = pen.UseCount;
            catalog[idx].LastUsedUtc = pen.LastUsedUtc;
        }
        else
        {
            pen.IsPresent = false;
            catalog.Add(pen);
        }
        await _catalog.SaveAllAsync(catalog, cancellationToken);

        // 交给协议域下发：显示序 MAC + BT_NAME（PC 零转换 —— 冻结基线 R9）
        _logger.LogInformation("Binding dispatch: pen {Mac} (source={Source}) -> 0x51 write",
            pen.MacDisplayText, pen.SourceName);

        return await _targetBindSender.SendBindTargetAsync(
            pen.MacDisplayOrder, pen.EffectiveBtName, cancellationToken);
    }

    public Task<BindingSendResult> StartNamePairingAsync(CancellationToken cancellationToken = default)
        => _targetBindSender.StartNamePairingAsync(cancellationToken);

    // ============ 笔端清绑定（0x51 PARAM=03，协议补充 01；仅串口直达） ============

    public async Task<byte> ClearPenBindingAsync(string portName,
        CancellationToken cancellationToken = default)
    {
        await EnsureSerialOpenAsync(portName, cancellationToken);

        var request = _codec.EncodePairClear();
        _logger.LogInformation("0x51/03 笔端清绑定（串口 {Port}）", portName);

        var resp = await _requester.RequestAsync(_serialChannel, request, CompanyCmd.PairInfo,
            cancellationToken).ConfigureAwait(false);

        if (resp == null)
            throw new InvalidOperationException(
                $"0x51/03 清绑定无应答（{portName} 超时）：确认串口接线与笔端在位（无应答时笔端状态不受影响）");

        if (!_codec.TryParsePairClearResp(resp, out var status))
            throw new InvalidOperationException("0x51/03 应答结构非法（缺 PARAM/STATUS）");

        _logger.LogInformation("0x51/03 清绑定应答 STATUS=0x{Status:X2}", status);
        return status;
    }
}
