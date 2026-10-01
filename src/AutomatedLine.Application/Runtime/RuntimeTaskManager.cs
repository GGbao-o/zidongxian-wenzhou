using AutomatedLine.Domain.Workpieces;

namespace AutomatedLine.Application.Runtime;

/// <summary>
/// 工件任务内存总表的实现。
/// 线程安全：内部用锁保护，因为后续状态机/Worker 可能在非 UI 线程增删，
/// 而界面刷新在 UI 线程读取（架构文档 §12.1）。
/// </summary>
public sealed class RuntimeTaskManager : IRuntimeTaskManager
{
    /// <summary>内部任务表。所有访问都必须持有 <see cref="_sync"/>。</summary>
    private readonly List<WorkpieceTask> _tasks = new();

    /// <summary>并发保护锁。</summary>
    private readonly object _sync = new();

    /// <summary>入线顺序号计数器（点"启动"时自增，作为入队尾的顺序）。</summary>
    private int _queueOrder;

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public WorkpieceTask Add(WorkpieceInfo info)
    {
        var task = new WorkpieceTask(info);

        lock (_sync)
        {
            _tasks.Add(task);
        }

        Console.WriteLine($"[RuntimeTaskManager] 新增任务 {task.Info}，当前共 {_tasks.Count} 个");

        // 先更新数据再通知，保证订阅者读到的快照已包含本次变更
        Changed?.Invoke(this, EventArgs.Empty);
        return task;
    }

    /// <inheritdoc />
    public bool Remove(Guid taskId)
    {
        bool removed;
        lock (_sync)
        {
            removed = _tasks.RemoveAll(t => t.Id == taskId) > 0;
        }

        if (removed)
        {
            Console.WriteLine($"[RuntimeTaskManager] 移除任务 {taskId}，当前共 {_tasks.Count} 个");
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    /// <inheritdoc />
    public bool Start(Guid taskId)
    {
        WorkpieceTask? task;
        lock (_sync)
        {
            task = _tasks.FirstOrDefault(t => t.Id == taskId);
            if (task is null || task.Runtime.TaskState is not (WorkpieceState.Created or WorkpieceState.Paused))
            {
                return false;
            }

            // 入队尾：分配递增顺序号
            task.Runtime.QueueOrder = Interlocked.Increment(ref _queueOrder);
            task.Runtime.TaskState = WorkpieceState.Waiting;
        }

        Console.WriteLine($"[RuntimeTaskManager] 启动任务 {taskId}，入线顺序 {task.Runtime.QueueOrder}");
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <inheritdoc />
    public bool Pause(Guid taskId)
    {
        WorkpieceTask? task;
        lock (_sync)
        {
            task = _tasks.FirstOrDefault(t => t.Id == taskId);
            if (task is null || task.Runtime.TaskState != WorkpieceState.Waiting)
            {
                return false;
            }

            // 出队：清空顺序号；再次启动会重新入队尾
            task.Runtime.QueueOrder = 0;
            task.Runtime.TaskState = WorkpieceState.Paused;
        }

        Console.WriteLine($"[RuntimeTaskManager] 暂停任务 {taskId}");
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <inheritdoc />
    public WorkpieceTask? Get(Guid taskId)
    {
        lock (_sync)
        {
            return _tasks.FirstOrDefault(t => t.Id == taskId);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<WorkpieceTask> Snapshot()
    {
        lock (_sync)
        {
            // ToList 在锁内完成，保证返回的列表是某一时刻的一致快照
            return _tasks.ToList();
        }
    }
}
