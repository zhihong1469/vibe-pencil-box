using PencilBox.Core.Ble;

namespace PencilBox.Core.Configuration;

/// <summary>
/// 已知笔端目录的本地持久化（解耦：接口在 Core，实现基于 JSON 文件在 Infrastructure）。
/// 目录项 <see cref="KnownPen"/> 以 MAC 显示序为单源；"扫到即保存"，设备暂不在场也能据此下发。
/// </summary>
public interface IPenCatalogStore
{
    /// <summary>加载全部已知笔端（文件不存在/损坏→空列表）</summary>
    Task<IReadOnlyList<KnownPen>> LoadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>保存全部已知笔端目录到本地</summary>
    Task SaveAllAsync(IEnumerable<KnownPen> pens, CancellationToken cancellationToken = default);

    /// <summary>
    /// 清空全部已知笔端记录（含使用统计/"上次使用"标记）。
    /// 仅清 PC 本地目录，**不影响**已下发到基站的绑定（那要走 A3 清目标）。
    /// </summary>
    Task ClearAllAsync(CancellationToken cancellationToken = default);
}
