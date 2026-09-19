using AutomatedLine.Domain.Spatial;
using AutomatedLine.Domain.Tasks;

namespace AutomatedLine.Domain.Cranes;

/// <summary>
/// 天车运行态。与不变的配置分离，只描述"这车现在在干什么"。
/// 单写者：<see cref="CurrentTask"/> 由调度器派发/Worker 完成时写入，<see cref="Step"/> 由对应 Worker 写入（架构文档 §12.3）。
/// </summary>
public sealed class CraneRuntime
{
    /// <summary>当前正式任务；无任务时为 null。</summary>
    public TransportTask? CurrentTask { get; set; }

    /// <summary>天车状态。</summary>
    public CraneState State { get; set; } = CraneState.Idle;

    /// <summary>状态机当前步骤。</summary>
    public TransportStep Step { get; set; } = TransportStep.Idle;

    /// <summary>当前持有的路权区间；未持有时为 null。</summary>
    public XInterval? LockedInterval { get; set; }
}
