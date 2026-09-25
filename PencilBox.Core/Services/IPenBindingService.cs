// <copyright file="IPenBindingService.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Ble;
using PencilBox.Core.Communication;

namespace PencilBox.Core.Services;

/// <summary>绑定发送结果（给 UI 反馈状态）</summary>
public record BindingSendResult(bool Success, string Message);

/// <summary>
/// 笔端绑定服务（取 MAC + 下发 门面）。
///
/// **两条来源、一份存储**（本接口的核心约定）：拿笔端 MAC 有两条路，目的完全相同——
/// 1. <see cref="ScanPensAsync"/>：PC 侧 BLE 广播扫描（拿广播地址；广播名里的尾号仅作展示/校验）；
/// 2. <see cref="ScanPenViaSerialAsync"/>：PC 串口直连笔端，发公司协议 `0x51` 读配对信息
///    （笔端恒回自身 MAC + `BT_NAME`）。
///
/// 两条路的产物都汇入**同一份** <see cref="KnownPen"/> 目录（见 <see cref="IPenCatalogStore"/>，
/// 显示序 MAC 为单源），因此后续"下发给基站"只有一条路径、与来源无关。
///
/// 目录模型（目标笔端不必当前在空气中）：只要拿到过（扫到/读到）即入库并持久化；
/// 之后即使设备暂不在场，也能据此直接把 MAC 交给 <see cref="ITargetBindSender"/> 下发
/// ——基站拿到 MAC 后，设备一出现即自动建联。
///
/// 三级分层（下拉框顺序：上次使用 &gt; 使用过 &gt; 出现过）：并入目录 → 统计使用次数/最近使用 →
/// 由 <see cref="KnownPenCatalog"/> 打层级。
///
/// 解耦边界：本接口只做"取 MAC + 目录"；帧封装 / 发送 / 真正下发由协议域
/// <see cref="ITargetBindSender"/> 负责（公司 V0.9 `0x51`/`0x52`）。
/// </summary>
public interface IPenBindingService
{
    /// <summary>
    /// 读取本地已知笔端目录（含三级层级，无扫描；用于启动时展示历史）。
    /// </summary>
    /// <returns>按"上次使用 &gt; 使用过 &gt; 出现过"降序的目录列表</returns>
    Task<IReadOnlyList<KnownPen>> LoadKnownPensAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 扫描 SunVote 笔端广播并合并进本地目录，返回带三级层级的目录全列表。
    /// </summary>
    /// <returns>按"上次使用 &gt; 使用过 &gt; 出现过"、层内在场+信号降序的目录列表</returns>
    Task<IReadOnlyList<KnownPen>> ScanPensAsync(int durationSec = 5,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// **串口直连笔端读 MAC**（公司协议 `0x51` 读配对信息）并合并进同一份目录，返回目录全列表。
    /// 与 BLE 扫描等价的第二条来源（工厂直连更可靠：不受广播/干扰影响，且能拿到笔端存档的 `BT_NAME`）。
    /// </summary>
    /// <param name="portName">笔端串口（如 `COM20`；`115200,N,8,1`，不施加 DTR/RTS）</param>
    Task<IReadOnlyList<KnownPen>> ScanPenViaSerialAsync(string portName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 清空本地已知笔端目录（**仅 PC 记录**：出现/使用历史全部抹掉；
    /// 已下发到基站的绑定不受影响，如需基站侧解绑走 A3 清目标）。
    /// 用于目录长期累积后的手动整理。
    /// </summary>
    Task ClearKnownPensAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 把选中笔端绑定到基站：标记"使用过/上次使用" → 取**显示序 MAC** → 交给协议域 <see cref="ITargetBindSender"/> 下发
    /// （公司 `0x51` 写；**PC 零转换**，冻结基线 R9）。
    /// </summary>
    /// <returns>下发结果</returns>
    Task<BindingSendResult> BindPenByMacAsync(KnownPen pen,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 让基站进入**名称配对**模式（`0x52` 进配对，匹配 `SunVote-R1#`）。
    /// </summary>
    Task<BindingSendResult> StartNamePairingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// **笔端清绑定**（`0x51 PARAM=03`，协议补充 01）——**仅串口直达路径**（补充 01 §1：
    /// 基站轨/USB 明确不实现，BLE 透传不提供，防工厂批量场景误清）。
    /// 受理 `00`（幂等）= 笔端将执行：删自身 BLE bond → 断连 → 进配对名视窗（60s，不复位）；
    /// `0xFF` = 笔端升级会话进行中拒绝（笔端状态不受影响）。
    /// </summary>
    /// <returns>笔端应答 STATUS（`0x00` 受理 / `0xFF` 会话占用 / 其他按笔端口径）</returns>
    Task<byte> ClearPenBindingAsync(string portName, CancellationToken cancellationToken = default);
}
