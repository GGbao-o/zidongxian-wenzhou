namespace AutomatedLine.Domain.Workpieces;

/// <summary>
///
/// 这个是根
/// 工件任务。把不可变的工件数据与可变的运行状态组合成一个内存对象，
/// 它同时也是"工件身份"的载体：任务被丢弃，工件身份即丢失（架构文档 §3.5）。
/// </summary>
public sealed class WorkpieceTask
{
    /// <summary>工件自身数据（不变）。</summary>
    public WorkpieceInfo Info { get; }

    /// <summary>工件运行状态（可变）。</summary>
    public WorkpieceRuntime Runtime { get; }

    /// <summary>
    /// 运行期唯一标识，等于 <see cref="Info"/> 的 RuntimeId，便于任务表与工件表对齐。
    /// </summary>
    public Guid Id => Info.RuntimeId;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="info">工件数据</param>
    /// <param name="runtime">运行状态；不传则新建一个（默认在队列、未派发）</param>
    public WorkpieceTask(WorkpieceInfo info, WorkpieceRuntime? runtime = null)
    {
        Info = info;
        Runtime = runtime ?? new WorkpieceRuntime();
    }
}
