using AutomatedLine.Domain.Routing;

namespace AutomatedLine.Domain.Workpieces;

/// <summary>
/// 工件运行状态（随流程变化）。
/// 与不可变的 <see cref="WorkpieceInfo"/> 分离，保证"工件数据"与"运行状态"职责单一。
/// </summary>
/// <remarks>
/// <see cref="TaskState"/>、<see cref="CurrentProcess"/> 会被"调度器 / CraneWorker / 生成器 / UI"跨线程读写，
/// 故用 <c>volatile</c> 字段保证可见性。<see cref="CurrentLocation"/> 是结构体，无法 volatile，由单点写入方负责一致性。
/// </remarks>
public sealed class WorkpieceRuntime
{
    /// <summary>当前工序 backing 字段（volatile）。</summary>
    private volatile ProcessType _currentProcess = ProcessType.Unknown;

    /// <summary>任务状态 backing 字段（volatile）。</summary>
    private volatile WorkpieceState _taskState = WorkpieceState.Created;

    /// <summary>当前工序。完成一道搬运后由状态机在放料确认时更新。</summary>
    public ProcessType CurrentProcess
    {
        get => _currentProcess;
        set => _currentProcess = value;
    }

    /// <summary>位置唯一事实来源。只允许天车状态机在取放料确认成功时单点写入。</summary>
    public WorkpieceLocation CurrentLocation { get; set; } = WorkpieceLocation.Queue;

    /// <summary>
    /// 任务状态。描述是否已启动/暂停/派发/执行中/完成/丢弃（架构文档 §11 WorkpieceState）。
    /// 初始为 <see cref="WorkpieceState.Created"/>（未启动，不生成候选）；人工点"启动"后才进入 <see cref="WorkpieceState.Waiting"/>。
    /// </summary>
    public WorkpieceState TaskState
    {
        get => _taskState;
        set => _taskState = value;
    }

    /// <summary>
    /// 入线顺序号（上料架取板顺序）。0 表示未入队（未启动/已暂停）；
    /// 点"启动"时分配递增序号入队尾，点"暂停"时清零，再次启动重新入队尾。
    /// </summary>
    public int QueueOrder { get; set; }
}
