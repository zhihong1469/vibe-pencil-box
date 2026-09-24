namespace PencilBox.Core.Fsm;

/// <summary>
/// 进程级 OTA 升级会话在位标志（pc-issues/03 C5）。
/// UI 宿主在升级会话运行期间置位，`CompanyPenCmdService` 据此让路（`0x54` 本地排队、会话终态后补发）；
/// CLI 单命令进程天然不并发，无需接线。
/// </summary>
public static class OtaSessionActivity
{
    /// <summary>升级会话进行中（UI 线程写、信封服务后台线程读，volatile 保证可见性）</summary>
    public static volatile bool InProgress;
}
