namespace AutomatedLine.Domain.Tasks;

/// <summary>
/// 搬运任务状态。描述正式任务在调度器/Worker 手中的阶段。
/// </summary>
public enum TransportTaskState
{
    /// <summary>已创建，尚未派发给天车。</summary>
    Created = 0,

    /// <summary>已派发给某台天车（路权与设备预约均已取得）。</summary>
    Assigned = 1,

    /// <summary>执行中，天车状态机正在推进。</summary>
    Running = 2,

    /// <summary>已完成。</summary>
    Completed = 3,

    /// <summary>已因异常中止（交由 TaskAbortCoordinator 处理）。</summary>
    Aborted = 4
}
