namespace AutomatedLine.Domain.Cranes;

/// <summary>
/// 天车运行状态。
/// 只保留实现中**真正会赋值**的三种：<see cref="Idle"/> / <see cref="Reserved"/> / <see cref="Running"/>。
/// 说明：
/// <list type="bullet">
/// <item>不存在"等待路权"状态——路权由状态机在开始执行任务时**原子获取**，拿不到就回滚、任务回
/// <c>Waiting</c>（架构文档 §8.4、§8.7.2）；</item>
/// <item>异常不给天车单独的"故障"状态——统一由 <c>TaskAbortCoordinator.Abort</c> 清理天车状态并把
/// **线路**置为故障暂停（§10.4、v2.51）。</item>
/// </list>
/// 对应架构文档 §11「CraneState」。
/// </summary>
public enum CraneState
{
    /// <summary>空闲。无任务。</summary>
    Idle = 0,

    /// <summary>已分配任务、该车 Worker 尚未出队执行。</summary>
    Reserved = 1,

    /// <summary>执行中。状态机正在推进搬运动作。</summary>
    Running = 2
}
