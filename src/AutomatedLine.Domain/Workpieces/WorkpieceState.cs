namespace AutomatedLine.Domain.Workpieces;

/// <summary>
/// 工件任务状态。
/// 描述一个 <c>WorkpieceTask</c> 从进入内存到终结的生命周期；与位置（<c>WorkpieceLocation</c>）正交。
/// 对应架构文档 §11「WorkpieceState」。
/// </summary>
public enum WorkpieceState
{
    /// <summary>等待中。已录入内存，但尚未被调度器派发。</summary>
    Waiting = 0,

    /// <summary>已派发。调度器已把该工件的搬运任务分配给某台天车，但天车可能尚未开始动作。</summary>
    Assigned = 1,

    /// <summary>执行中。天车状态机正在推进该工件的搬运。</summary>
    Running = 2,

    /// <summary>已完成。工件已按工艺走完全部工序并下线。</summary>
    Completed = 3,

    /// <summary>已丢弃。因硬故障被 TaskAbortCoordinator 丢弃，内存身份随之丢失（刻意设计）。</summary>
    Discarded = 4
}
