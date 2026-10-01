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
    /// 新增一个工件任务，默认位于队列、未派发。
    /// </summary>
    /// <param name="info">工件数据</param>
    /// <returns>创建好的任务</returns>
    WorkpieceTask Add(WorkpieceInfo info);

    /// <summary>
    /// 按任务标识移除任务（丢弃或人工清除等待任务）。
    /// </summary>
    /// <param name="taskId">任务标识</param>
    /// <returns>移除成功返回 true；不存在返回 false</returns>
    bool Remove(Guid taskId);

    /// <summary>
    /// 启动任务：未启动/已暂停 → 排队中，并分配**入线顺序号**（入队尾）。
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
