using AutomatedLine.Domain.Routing;

namespace AutomatedLine.Domain.Workpieces;

/// <summary>
/// 工件运行状态（随流程变化）。
/// 与不可变的 <see cref="WorkpieceInfo"/> 分离，保证"工件数据"与"运行状态"职责单一。
/// </summary>
/// <remarks>
/// <see cref="TaskState"/>、<see cref="CurrentProcess"/> 会被"调度器 / CraneWorker / 生成器 / UI"跨线程读写，
/// 故用 <c>Volatile</c> 保证可见性、<c>Interlocked</c> 保证状态迁移原子。<see cref="CurrentLocation"/> 是结构体，
/// 由单点写入方负责一致性。
/// </remarks>
public sealed class WorkpieceRuntime
{
    /// <summary>当前工序 backing 字段（volatile）。</summary>
    private volatile ProcessType _currentProcess = ProcessType.Unknown;

    /// <summary>任务状态 backing 字段（int，供 Interlocked/Volatile 使用）。</summary>
    private int _taskState = (int)WorkpieceState.Created;

    /// <summary>完成时间的UTC ticks；-1表示尚未通过正常完成入口记录，避免未知时间的任务被自动删除。</summary>
    private long _completedAtUtcTicks = -1;

    /// <summary>工件最终完成时间；仅供完成记录保留期限使用，不是每段搬运的结束时间。</summary>
    public DateTimeOffset? CompletedAtUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _completedAtUtcTicks);
            return ticks < 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>在最终收尾成功时记录完成时间并发布Completed，保证清理线程不会先看到完成状态而缺少时间。</summary>
    /// <param name="completedAt">最终完成时刻；按UTC保存。</param>
    public void MarkCompleted(DateTimeOffset completedAt)
    {
        Interlocked.Exchange(ref _completedAtUtcTicks, completedAt.UtcDateTime.Ticks);
        TaskState = WorkpieceState.Completed;
    }

    /// <summary>当前工序。完成一道搬运后由状态机在放料确认时更新。</summary>
    public ProcessType CurrentProcess
    {
        get => _currentProcess;
        set => _currentProcess = value;
    }

    /// <summary>位置唯一事实来源。只允许天车状态机在取放料确认成功时单点写入。</summary>
    public WorkpieceLocation CurrentLocation { get; set; } = WorkpieceLocation.Queue;

    // 引用只发布一次；读者无需锁即可读取完整不可变路线。
    private ProcessPlanSnapshot? _processPlan;

    /// <summary>首次启动锁定的工艺；未启动时为空，暂停恢复和搬运回退均保留原对象。</summary>
    public ProcessPlanSnapshot? ProcessPlan => Volatile.Read(ref _processPlan);

    /// <summary>仅允许第一次绑定工艺，后续调用不能覆盖已经启动或在设备上的工件路线。</summary>
    /// <param name="plan">首次启动捕获的独立路线快照。</param>
    public void LockProcessPlan(ProcessPlanSnapshot plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Interlocked.CompareExchange(ref _processPlan, plan, null);
    }

    /// <summary>
    /// 任务状态。描述是否已启动/暂停/派发/执行中/完成/丢弃（架构文档 §11 WorkpieceState）。
    /// 初始为 <see cref="WorkpieceState.Created"/>（未启动，不生成候选）；人工启动或ERP自动入队后进入 <see cref="WorkpieceState.Waiting"/>。
    /// </summary>
    public WorkpieceState TaskState
    {
        get => (WorkpieceState)Volatile.Read(ref _taskState);
        set => Volatile.Write(ref _taskState, (int)value);
    }

    /// <summary>
    /// 原子迁移任务状态：仅当当前等于 <paramref name="expected"/> 时改为 <paramref name="next"/>。
    /// 用于调度器把 `Waiting → Assigned` **只成功一次**，避免同一任务被并发派发。
    /// </summary>
    /// <param name="expected">期望的当前状态</param>
    /// <param name="next">要迁移到的状态</param>
    /// <returns>迁移成功返回 true；当前状态不是 expected 返回 false</returns>
    public bool TryTransition(WorkpieceState expected, WorkpieceState next)
        => Interlocked.CompareExchange(ref _taskState, (int)next, (int)expected) == (int)expected;

    /// <summary>
    /// 入线顺序号（上料架取板顺序）。0 表示未入队（未启动/已暂停）；
    /// 人工启动或ERP按文件行序自动入队时分配递增序号，点"暂停"时清零，再次启动重新入队尾。
    /// </summary>
    public int QueueOrder { get; set; }
}
