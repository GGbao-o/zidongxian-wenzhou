namespace AutomatedLine.Domain.Workpieces;

/// <summary>
///
/// 这个是根
/// 工件任务。把不可变的工件数据与可变的运行状态组合成一个内存对象，
/// 它同时也是"工件身份"的载体：任务被丢弃，工件身份即丢失（架构文档 §3.5）。
/// </summary>
public sealed class WorkpieceTask
{
    /// <summary>
    /// 工件自身数据。
    /// 对外只读；**未入线时**可由 <see cref="ReplaceInfo"/> 整体替换（"编辑任务"功能），
    /// 采用整体替换而不是逐字段改，避免出现"改到一半"的中间态被读到。
    /// </summary>
    public WorkpieceInfo Info { get; private set; }

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

    /// <summary>
    /// 整体替换工件数据（"编辑任务"功能）。
    /// **只允许未入线任务**，由 <c>IRuntimeTaskManager.TryUpdate</c> 统一把关。
    /// 调用方必须保证 <paramref name="info"/> 的 <c>RuntimeId</c> 与原数据一致：
    /// 任务身份由 <c>RuntimeId</c> 标识，换了它就等于换了一块板（任务表/设备预约/天车引用会全部对不上）。
    /// </summary>
    /// <param name="info">新的工件数据（RuntimeId 必须沿用）</param>
    internal void ReplaceInfo(WorkpieceInfo info) => Info = info;
}
