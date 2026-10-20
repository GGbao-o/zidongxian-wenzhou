using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Routing;
using AutomatedLine.Domain.Diagnostics;
using AutomatedLine.Domain.Routing;
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

    /// <summary>人工启动和ERP自动入队共用的顺序号计数器，保证新任务统一追加队尾。</summary>
    private int _queueOrder;

    /// <summary>日志。人工"编辑任务"要写 operation.txt，便于事后追溯"哪块板什么时候被谁改过"。</summary>
    private readonly IAppLogger _logger;
    /// <summary>只在首次启动捕获模板，恢复时不覆盖工件原工艺。</summary>
    private readonly IProcessPlanService _processPlans;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="logger">日志</param>
    /// <param name="processPlans">首次启动捕获最新工艺的共享服务。</param>
    public RuntimeTaskManager(IAppLogger logger, IProcessPlanService processPlans)
    {
        _logger = logger;
        _processPlans = processPlans;
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
    public WorkpieceTask? TryAddImported(WorkpieceInfo info)
        => TryAddImportedBatch(new[] { info })[0];

    /// <inheritdoc />
    public IReadOnlyList<WorkpieceTask?> TryAddImportedBatch(IReadOnlyList<WorkpieceInfo> infos)
    {
        var results = new WorkpieceTask?[infos.Count];
        if (infos.Count == 0) return results;
        var created = new List<(WorkpieceTask Task, int Count, int QueueOrder)>();
        // 一批只持锁一次：按文件行序去重并完成入队，避免并发批次/人工启动插入本批中间。
        lock (_sync)
        {
            var identities = _tasks.Select(t => (t.Info.PlateNo, t.Info.SerialNo)).ToHashSet();
            ProcessPlanSnapshot? batchPlan = null;
            for (var index = 0; index < infos.Count; index++)
            {
                var info = infos[index];
                if (!identities.Add((info.PlateNo, info.SerialNo))) continue;
                // 第一件成功项才读取，整批共用不可变快照；全重复批次无需重新校验当前模板。
                batchPlan ??= _processPlans.Capture();
                var task = new WorkpieceTask(info);
                task.Runtime.LockProcessPlan(batchPlan);
                // 在发布到任务表前完成启动，生成器和界面不会观察到尚未入队的ERP任务。
                task.Runtime.QueueOrder = Interlocked.Increment(ref _queueOrder);
                task.Runtime.TaskState = WorkpieceState.Waiting;
                _tasks.Add(task);
                results[index] = task;
                created.Add((task, _tasks.Count, task.Runtime.QueueOrder));
            }
        }
        if (created.Count == 0) return results;
        foreach (var entry in created)
        {
            var info = entry.Task.Info;
            _logger.Info(LogCategory.Task, $"ERP任务创建并自动启动：工件 {info}（{Describe(info)}），入队状态=Waiting，入线顺序={entry.QueueOrder}，当前共 {entry.Count} 个；锁定工艺={entry.Task.Runtime.ProcessPlan!.Version}，路线={entry.Task.Runtime.ProcessPlan.Description}");
        }
        // 仍保留每件生命周期日志，只合并列表通知；通知失败不能改变已经提交的成功结果。
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { _logger.Warn(LogCategory.Operation, $"ERP批量{created.Count}件已添加，列表通知失败：{ex.Message}"); }
        return results;
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

    /// <inheritdoc />批量删除到达保留期限的Completed工件；同一锁内清匹配引用和移除，锁外只通知一次。
    public IReadOnlyList<WorkpieceTask> RemoveCompleted(DateTimeOffset completedBeforeUtc, DeviceRegistry devices, CraneRegistry cranes)
    {
        var removed = new List<(WorkpieceTask Task, int Devices, int Cranes)>();
        lock (_sync)
        {
            // 与ERP入队使用同一把任务表锁；先清旧引用再移除，使同身份重收只发生在删除提交之后。
            var expired = _tasks.Where(t => t.Runtime.TaskState == WorkpieceState.Completed
                && t.Runtime.CompletedAtUtc is { } completedAt && completedAt <= completedBeforeUtc).ToArray();
            foreach (var task in expired)
            {
                var deviceCount = devices.ClearWorkpiece(task.Id);
                var craneCount = cranes.ClearWorkpiece(task.Id);
                removed.Add((task, deviceCount, craneCount));
            }
            // 一次删除避免逐件List.Remove的平方级开销；所有引用清理成功后才提交移除。
            if (expired.Length > 0)
            {
                var ids = expired.Select(task => task.Id).ToHashSet();
                _tasks.RemoveAll(task => ids.Contains(task.Id));
            }
        }
        if (removed.Count == 0) return Array.Empty<WorkpieceTask>();

        foreach (var entry in removed)
            _logger.Info(LogCategory.Task, $"自动删除已完成任务：工件 {entry.Task.Info}，工件Id={entry.Task.Id:N}，完成时间={entry.Task.Runtime.CompletedAtUtc:O}，清理截止={completedBeforeUtc:O}，已清设备引用={entry.Devices}、天车引用={entry.Cranes}；同版号同序号允许再次接收");
        // 回调可能同步进入UI或重新查询任务表，必须在所有内部锁释放之后且每批只调用一次。
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { _logger.Warn(LogCategory.Operation, $"已自动删除{removed.Count}件完成任务，页面通知失败，等待后续刷新：{ex.Message}"); }
        return removed.Select(entry => entry.Task).ToArray();
    }

    /// <inheritdoc />  主页面对应工件启动按钮和新方法
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
            //首次启动：锁定工艺快照（核心分支）
            if (task.Runtime.ProcessPlan is null)
            {
                if (task.Runtime.TaskState != WorkpieceState.Created || task.Runtime.CurrentLocation.Kind != WorkpieceLocationKind.Queue)
                    throw new InvalidOperationException("已启动或在设备上的工件缺少原工艺快照，不能使用新模板覆盖");
                //_processPlans.Capture() 捕获当前最新工艺模板的快照；  LockProcessPlan 把它固化到该任务 Runtime 上（不可再变）；
                task.Runtime.LockProcessPlan(_processPlans.Capture());
            }
            // 暂停恢复只重新排队，工艺沿用首次启动快照。
            task.Runtime.QueueOrder = Interlocked.Increment(ref _queueOrder);
            task.Runtime.TaskState = WorkpieceState.Waiting;
        }

        Console.WriteLine($"[RuntimeTaskManager] 启动任务 {taskId}，入线顺序 {task.Runtime.QueueOrder}");
        // task.txt：任务入线（Created/Paused → Waiting），并记录入线顺序，便于对照 scheduling.txt 的派发顺序
        _logger.Info(LogCategory.Task, $"任务开始：工件 {task.Info}，入线顺序 {task.Runtime.QueueOrder}（状态 → Waiting）；锁定工艺={task.Runtime.ProcessPlan!.Version}，路线={task.Runtime.ProcessPlan.Description}");
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
            // 与调度器竞争同一个 Waiting 状态；派发先成功时不能再覆盖归属或清掉排位。
            if (task is null || !task.Runtime.TryTransition(WorkpieceState.Waiting, WorkpieceState.Paused))
            {
                return false;
            }

            // 仅成功暂停才出队；再次启动仍按原有规则重新入队尾。
            task.Runtime.QueueOrder = 0;
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

            // 【准入判据】只允许尚未启动入线（Created）的任务改数据，ERP自动入队后也不允许编辑。
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
        => $"直径={info.Diameter} 长度={info.Length} 堵厚={info.PlugThickness} " +
           $"堵孔={info.PlugHole} 打印内容={info.PrintContent} 斜床工艺={info.LatheMachiningMode}";

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
