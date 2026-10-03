namespace AutomatedLine.Domain.Workpieces;

/// <summary>
/// 工件任务状态。
/// 描述一个 <c>WorkpieceTask</c> 从进入内存到终结的生命周期；与位置（<c>WorkpieceLocation</c>）正交。
/// 对应架构文档 §11「WorkpieceState」。
/// </summary>
public enum WorkpieceState
{
    /// <summary>等待中。已"启动"并进入队列，可被生成器生成候选/被调度器派发。</summary>
    Waiting = 0,

    /// <summary>已派发。调度器已把该工件的搬运任务分配给某台天车，但天车可能尚未开始动作。</summary>
    Assigned = 1,

    /// <summary>执行中。天车状态机正在推进该工件的搬运。</summary>
    Running = 2,

    /// <summary>已完成。工件已按工艺走完全部工序并下线。</summary>
    Completed = 3,

    /// <summary>
    /// 已丢弃。因异常被 `TaskAbortCoordinator` 标记丢弃：**保留记录便于追溯**，
    /// 但设备/天车对该工件的引用已清除、位置置 `Discarded`；不再参与生成，人工可将该任务从列表移除。
    /// </summary>
    Discarded = 4,

    /// <summary>已创建/未启动。已录入内存，但人工尚未点"启动"，**不生成候选**。</summary>
    Created = 5,

    /// <summary>已暂停。人工暂停该任务，**不生成候选**，点"启动"后回到 <see cref="Waiting"/>。</summary>
    Paused = 6
}
