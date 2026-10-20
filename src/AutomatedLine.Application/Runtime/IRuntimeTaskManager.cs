using AutomatedLine.Domain.Workpieces;

namespace AutomatedLine.Application.Runtime;

/// <summary>
/// 全线工件任务内存总表（唯一）抽象（架构文档 §7）。
/// 只保存运行态于内存，程序关闭即消失，这是刻意设计（架构文档 §3.4）。
/// 之所以抽接口：ViewModel 与后续调度/状态机都依赖它，但不直接依赖具体容器实现。
/// </summary>
public interface IRuntimeTaskManager
{
    /// <summary>任务集合发生变化（增/删/状态变更）时触发，供界面刷新。</summary>
    event EventHandler? Changed;

    /// <summary>
    /// 人工新增一个工件任务，默认位于队列、状态Created，需另行启动。
    /// </summary>
    /// <param name="info">工件数据</param>
    /// <returns>创建好的任务</returns>
    WorkpieceTask Add(WorkpieceInfo info);

    /// <summary>ERP专用原子添加并自动入队：同版号和序号已存在时拒绝；成功为Waiting，不改变人工添加规则。</summary>
    /// <param name="info">ERP工件原始数据</param>
    /// <returns>新增任务；业务身份重复返回null</returns>
    WorkpieceTask? TryAddImported(WorkpieceInfo info);

    /// <summary>ERP整批原子去重，共用本批首次启动工艺快照，按文件行序入队；全批只通知一次。</summary>
    /// <param name="infos">按文件顺序解析出的工件；成功项分配递增QueueOrder并置Waiting，追加已有队尾</param>
    /// <returns>与输入长度/顺序一致的结果，重复身份对应null；无新增时不通知</returns>
    IReadOnlyList<WorkpieceTask?> TryAddImportedBatch(IReadOnlyList<WorkpieceInfo> infos);

    /// <summary>
    /// 按任务标识移除任务（丢弃或人工清除等待任务）。
    /// </summary>
    /// <param name="taskId">任务标识</param>
    /// <returns>移除成功返回 true；不存在返回 false</returns>
    bool Remove(Guid taskId);

    /// <summary>批量删除到达保留期限的Completed工件；同一锁内清匹配引用和移除，锁外只通知一次。</summary>
    /// <param name="completedBeforeUtc">完成时间不晚于此时刻才清理；缺少完成时间的记录保留。</param>
    /// <param name="devices">原子清除相同工件ID引用的设备目录。</param>
    /// <param name="cranes">原子清除相同工件ID引用的天车目录；不修改搬运任务。</param>
    /// <returns>本批实际移除的工件；无删除时不发送Changed通知。</returns>
    IReadOnlyList<WorkpieceTask> RemoveCompleted(DateTimeOffset completedBeforeUtc, DeviceRegistry devices, CraneRegistry cranes);

    /// <summary>
    /// 启动任务：首次启动锁定最新工艺，暂停恢复沿用原快照；转为排队中并分配入线顺序。
    /// </summary>
    /// <param name="taskId">任务标识</param>
    /// <returns>成功返回 true；任务不存在或状态不允许返回 false</returns>
    bool Start(Guid taskId);

    /// <summary>
    /// 暂停任务：排队中 → 已暂停，并清空入线顺序号（出队）。
    /// </summary>
    /// <param name="taskId">任务标识</param>
    /// <returns>成功返回 true；任务不存在或状态不允许返回 false</returns>
    bool Pause(Guid taskId);

    /// <summary>
    /// 修改**未入线**任务的工件数据（"编辑任务"）。
    /// **只有 `Created`（尚未启动入线）才允许**：一旦人工启动或ERP自动入队，工件可能已被取走或已按旧参数加工过，
    /// 此时改数据会造成"设备按旧参数加工、后续工序按新参数加工"的错配。
    /// 实现必须**原子整体替换**工件数据（不逐字段改），且沿用原 <c>RuntimeId</c>（任务身份不变）。
    /// </summary>
    /// <param name="taskId">任务标识</param>
    /// <param name="info">新的工件数据（RuntimeId 沿用原值）</param>
    /// <returns>成功返回 true；任务不存在或状态不允许（已入线）返回 false</returns>
    bool TryUpdate(Guid taskId, WorkpieceInfo info);

    /// <summary>
    /// 按任务标识查找任务。
    /// </summary>
    /// <param name="taskId">任务标识</param>
    /// <returns>任务；不存在返回 null</returns>
    WorkpieceTask? Get(Guid taskId);

    /// <summary>
    /// 获取当前全部任务的快照。
    /// 返回快照而不是内部集合，避免调用方在遍历时被并发修改影响。
    /// </summary>
    /// <returns>任务列表快照</returns>
    IReadOnlyList<WorkpieceTask> Snapshot();
}
