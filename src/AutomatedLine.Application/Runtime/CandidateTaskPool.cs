using AutomatedLine.Domain.Tasks;

namespace AutomatedLine.Application.Runtime;

/// <summary>
/// 候选任务池（架构文档 §8.3）。
/// 采用 Snapshot / Rebuild 模式：每周期由 TransportTaskGenerator 整体重建，不维护脆弱的任务队列。
/// 候选任务不代表已派发，随时可基于当前状态重算。
/// </summary>
public sealed class CandidateTaskPool
{
    /// <summary>内部候选任务列表。</summary>
    private readonly List<CandidateTask> _tasks = new();

    /// <summary>并发保护锁。</summary>
    private readonly object _sync = new();

    /// <summary>候选池整体更新时触发，供界面刷新。</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// 用新一批候选任务整体替换旧内容（Rebuild）。
    /// </summary>
    /// <param name="tasks">新的候选任务集合</param>
    public void Replace(IEnumerable<CandidateTask> tasks)
    {
        lock (_sync)
        {
            _tasks.Clear();
            _tasks.AddRange(tasks);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 清空候选池。
    /// </summary>
    public void Clear()
    {
        lock (_sync)
        {
            _tasks.Clear();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 获取候选任务快照。
    /// </summary>
    /// <returns>候选任务列表快照</returns>
    public IReadOnlyList<CandidateTask> Snapshot()
    {
        lock (_sync)
        {
            return _tasks.ToList();
        }
    }
}
