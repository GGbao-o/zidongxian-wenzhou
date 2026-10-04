using AutomatedLine.Domain.Spatial;
using AutomatedLine.Domain.Tasks;

namespace AutomatedLine.Domain.Cranes;

/// <summary>
/// 天车运行态。与不变的配置分离，只描述"这车现在在干什么"。
/// 单写者：<see cref="CurrentTask"/> 由调度器派发、该车 Worker 出队/完成时写入（架构文档 §12.3）。
/// </summary>
/// <remarks>
/// <see cref="State"/> 与 <see cref="CurrentTask"/> 会被"调度器 / CraneWorker / 异常协调器"
/// 在不同线程读写，故用 <c>volatile</c> 字段保证"写入后其它线程立刻可见"、避免读到旧值。
/// <see cref="LockedInterval"/> 是结构体（<see cref="XInterval"/>?），无法 volatile，仅由单写者线程判断后使用。
/// </remarks>
public sealed class CraneRuntime
{
    /// <summary>当前任务 backing 字段（volatile）。</summary>
    private volatile TransportTask? _currentTask;

    /// <summary>天车状态 backing 字段（volatile）。</summary>
    private volatile CraneState _state = CraneState.Idle;

    /// <summary>当前正式任务；无任务时为 null。</summary>
    public TransportTask? CurrentTask
    {
        get => _currentTask;
        set => _currentTask = value;
    }

    /// <summary>天车状态。</summary>
    public CraneState State
    {
        get => _state;
        set => _state = value;
    }

    /// <summary>当前持有的路权区间；未持有时为 null。（结构体，不能用 volatile）</summary>
    public XInterval? LockedInterval { get; set; }
}
