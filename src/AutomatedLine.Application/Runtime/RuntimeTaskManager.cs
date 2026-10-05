using AutomatedLine.Application.Abstractions;
using AutomatedLine.Domain.Diagnostics;
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

    /// <summary>日志。人工"编辑任务"要写 operation.txt，便于事后追溯"哪块板什么时候被谁改过"。</summary>
    private readonly IAppLogger _logger;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="logger">日志</param>
    public RuntimeTaskManager(IAppLogger logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public WorkpieceTask Add(WorkpieceInfo info)
    {
        var task = new WorkpieceTask(info);

        int count;
        lock (_sync)
        {
            _tasks.Add(task);
            count = _tasks.Count;
        }

        Console.WriteLine($"[RuntimeTaskManager] 新增任务 {task.Info}，当前共 {count} 个");

        // task.txt：任务生命周期**起点**。与"开始/暂停/完成/丢弃"合成一条时间线，
        // 排查某块板只需看 task.txt，不必在 operation / scheduling / statemachine 之间来回找。
        _logger.Info(LogCategory.Task, $"任务创建：工件 {task.Info}（{Describe(info)}），当前共 {count} 个");

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
        // task.txt：任务入线（Created/Paused → Waiting），并记录入线顺序，便于对照 scheduling.txt 的派发顺序
        _logger.Info(LogCategory.Task, $"任务开始：工件 {task.Info}，入线顺序 {task.Runtime.QueueOrder}（状态 → Waiting）");
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
        // task.txt：任务出队暂停（Waiting → Paused，顺序号已清空，再次启动会重新入队尾）
        _logger.Info(LogCategory.Task, $"任务暂停：工件 {task.Info}（状态 → Paused，已出队）");
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <inheritdoc />
    public bool TryUpdate(Guid taskId, WorkpieceInfo info)
    {
        WorkpieceTask? task;
        string before;
        lock (_sync)
        {
            task = _tasks.FirstOrDefault(t => t.Id == taskId);

            // 【准入判据】只允许"添加后从未启动"（Created）的任务改数据。
            // 一旦入线（Waiting/Paused/已派发/执行中…），工件可能已被取走、或已按旧参数加工过，
            // 再改数据会让"设备按旧参数加工、后续工序按新参数加工"错配。
            if (task is null || task.Runtime.TaskState != WorkpieceState.Created)
            {
                return false;
            }

            before = Describe(task.Info);

            // 原子整体替换（不逐字段改），且 RuntimeId 已由调用方沿用 → 任务身份不变
            task.ReplaceInfo(info);
        }

        // 锁外写日志与通知，避免持锁回调外部代码
        _logger.Info(LogCategory.Operation, $"编辑任务 {Short(taskId)}：{before} → {Describe(info)}");
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>工件数据摘要（编辑日志用：只列可编辑字段，便于前后对比）。</summary>
    /// <param name="info">工件数据</param>
    /// <returns>摘要文本</returns>
    private static string Describe(WorkpieceInfo info)
        => $"直径={info.Diameter} 长度={info.Length} 左堵厚={info.LeftPlugThickness} 右堵厚={info.RightPlugThickness} " +
           $"堵孔={info.PlugHole} 打印内容={info.PrintContent} 内孔锥度={info.InnerHoleTaper} " +
           $"圆角={info.FilletSize} 斜床工艺={info.LatheMachiningMode}";

    /// <summary>任务标识短文本，用于日志。</summary>
    /// <param name="taskId">任务标识</param>
    /// <returns>前 8 位</returns>
    private static string Short(Guid taskId) => taskId.ToString("N")[..8];

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
