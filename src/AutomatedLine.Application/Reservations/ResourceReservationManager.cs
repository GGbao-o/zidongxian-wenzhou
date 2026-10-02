using AutomatedLine.Application.Abstractions;
using AutomatedLine.Domain.Diagnostics;

namespace AutomatedLine.Application.Reservations;

/// <summary>
/// 设备预约管理器（架构文档 §8.6）。
/// 管理"正式任务占用哪些设备资源"，是设备预约状态的**唯一来源**；设备对象本身不保存 IsReserved。
/// 设备预约与空间路权是两回事：一个管设备归属，一个管 X 区间互斥。
/// </summary>
public sealed class ResourceReservationManager
{
    /// <summary>任务标识 → (源设备号, 目标设备号)。源为 null 表示源预约已提前释放。</summary>
    private readonly Dictionary<Guid, (int? Source, int Target)> _byTask = new();

    /// <summary>设备号 → 占用它的任务标识。</summary>
    private readonly Dictionary<int, Guid> _byDevice = new();

    /// <summary>并发保护锁。</summary>
    private readonly object _sync = new();

    /// <summary>日志。</summary>
    private readonly IAppLogger _logger;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="logger">日志</param>
    public ResourceReservationManager(IAppLogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 尝试预约源与目标设备。任一已被预约则整体失败（不部分占用）。
    /// </summary>
    /// <param name="taskId">任务标识</param>
    /// <param name="sourceId">源设备号</param>
    /// <param name="targetId">目标设备号</param>
    /// <returns>预约成功返回 true</returns>
    public bool TryReserve(Guid taskId, int sourceId, int targetId)
    {
        if (sourceId == targetId)
        {
            return false;
        }

        lock (_sync)
        {
            if (_byDevice.ContainsKey(sourceId) || _byDevice.ContainsKey(targetId))
            {
                return false;
            }

            _byDevice[sourceId] = taskId;
            _byDevice[targetId] = taskId;
            _byTask[taskId] = (sourceId, targetId);
        }

        _logger.Info(LogCategory.Scheduling, $"预约设备：任务 {Short(taskId)} → 源{sourceId}、目标{targetId}");
        return true;
    }

    /// <summary>
    /// 释放任务占用的全部设备预约。幂等。
    /// </summary>
    /// <param name="taskId">任务标识</param>
    /// <returns>确实释放了返回 true</returns>
    public bool Release(Guid taskId)
    {
        bool released;
        lock (_sync)
        {
            released = _byTask.Remove(taskId, out var devices);
            if (released)
            {
                if (devices.Source is int sourceId)
                {
                    _byDevice.Remove(sourceId);
                }

                _byDevice.Remove(devices.Target);
            }
        }

        if (released)
        {
            _logger.Info(LogCategory.Scheduling, $"释放预约：任务 {Short(taskId)}");
        }

        return released;
    }

    /// <summary>
    /// 只释放任务的**源设备**预约，保留目标设备预约。用于"到达目标后释放源"。
    /// 源已释放或任务不存在返回 false（幂等）。
    /// </summary>
    /// <param name="taskId">任务标识</param>
    /// <returns>确实释放了返回 true</returns>
    public bool ReleaseSource(Guid taskId)
    {
        bool released = false;
        lock (_sync)
        {
            if (_byTask.TryGetValue(taskId, out var devices) && devices.Source is int sourceId)
            {
                _byDevice.Remove(sourceId);
                _byTask[taskId] = (null, devices.Target);
                released = true;
            }
        }

        if (released)
        {
            _logger.Info(LogCategory.Scheduling, $"释放源预约：任务 {Short(taskId)}");
        }

        return released;
    }

    /// <summary>
    /// 判断设备是否已被任一任务预约。
    /// </summary>
    /// <param name="deviceId">设备号</param>
    /// <returns>已预约返回 true</returns>
    public bool IsReserved(int deviceId)
    {
        lock (_sync)
        {
            return _byDevice.ContainsKey(deviceId);
        }
    }

    /// <summary>
    /// 获取"设备号 → 任务"的快照（供展示/排查）。
    /// </summary>
    /// <returns>预约快照</returns>
    public IReadOnlyDictionary<int, Guid> Snapshot()
    {
        lock (_sync)
        {
            return new Dictionary<int, Guid>(_byDevice);
        }
    }

    /// <summary>任务标识短文本，用于日志。</summary>
    /// <param name="taskId">任务标识</param>
    /// <returns>前 8 位</returns>
    private static string Short(Guid taskId) => taskId.ToString("N")[..8];
}
