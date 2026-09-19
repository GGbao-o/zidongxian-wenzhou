using AutomatedLine.Application.Abstractions;
using AutomatedLine.Domain.Diagnostics;
using AutomatedLine.Domain.Spatial;

namespace AutomatedLine.Application.RightOfWay;

/// <summary>
/// 任务 X 区间路权互斥管理器（架构文档 §8.7）。
/// 一个正在执行的任务 = 一把区间锁；获取是**原子操作**，与已锁集合求交，相交即失败。
/// 路权统一在**派发时**由调度器获取（§8.4），任务回安全位后释放；状态机内不再获取。
/// </summary>
public sealed class RightOfWayManager
{
    /// <summary>任务标识 → 已锁区间。</summary>
    private readonly Dictionary<Guid, XInterval> _locks = new();

    /// <summary>并发保护锁。整个 TryAcquire 在锁内完成，保证"判断即加锁"没有空档。</summary>
    private readonly object _sync = new();

    /// <summary>日志。</summary>
    private readonly IAppLogger _logger;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="logger">日志</param>
    public RightOfWayManager(IAppLogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 原子获取路权区间。与任何已锁区间相交则失败。
    /// </summary>
    /// <param name="taskId">任务标识</param>
    /// <param name="interval">任务区间</param>
    /// <returns>获取成功返回 true；相交返回 false</returns>
    public bool TryAcquire(Guid taskId, XInterval interval)
    {
        lock (_sync)
        {
            foreach (var existing in _locks.Values)
            {
                if (existing.Overlaps(interval))
                {
                    // 相交：本周期不获取，由调度器改派或下周期重试
                    return false;
                }
            }

            _locks[taskId] = interval;
            _logger.Info(LogCategory.Scheduling, $"获取路权：任务 {Short(taskId)} 区间 {interval}");
            return true;
        }
    }

    /// <summary>
    /// 释放任务路权。幂等。
    /// </summary>
    /// <param name="taskId">任务标识</param>
    /// <returns>确实释放了返回 true；本来就不持有返回 false</returns>
    public bool Release(Guid taskId)
    {
        lock (_sync)
        {
            if (_locks.Remove(taskId))
            {
                _logger.Info(LogCategory.Scheduling, $"释放路权：任务 {Short(taskId)}");
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// 查询任务是否持有路权。
    /// </summary>
    /// <param name="taskId">任务标识</param>
    /// <returns>持有返回 true</returns>
    public bool IsHeld(Guid taskId)
    {
        lock (_sync)
        {
            return _locks.ContainsKey(taskId);
        }
    }

    /// <summary>
    /// 获取当前已锁区间快照（供大屏展示）。
    /// </summary>
    /// <returns>已锁区间列表</returns>
    public IReadOnlyList<XInterval> Snapshot()
    {
        lock (_sync)
        {
            return _locks.Values.ToList();
        }
    }

    /// <summary>任务标识短文本，用于日志。</summary>
    /// <param name="taskId">任务标识</param>
    /// <returns>前 8 位</returns>
    private static string Short(Guid taskId) => taskId.ToString("N")[..8];
}
