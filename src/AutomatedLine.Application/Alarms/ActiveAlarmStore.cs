using AutomatedLine.Domain.Alarms;

namespace AutomatedLine.Application.Alarms;

/// <summary>
/// 活动报警内存存储。报警只存内存，页面直接读它，保证刷新快；明细另由 AlarmManager 写磁盘。
/// 相同来源 + 相同内容的报警会合并并累加次数，避免高频异常刷屏。
/// </summary>
public sealed class ActiveAlarmStore
{
    /// <summary>内部报警列表（最新在前）。</summary>
    private readonly List<ActiveAlarm> _alarms = new();

    /// <summary>并发保护锁。</summary>
    private readonly object _sync = new();

    /// <summary>报警集合发生变化时触发，供界面刷新。</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// 新增或合并一条报警。
    /// </summary>
    /// <param name="alarm">报警</param>
    /// <returns>本次合并后的稳定显示快照，供自动弹窗使用相同次数、时间和诊断。</returns>
    public ActiveAlarm Raise(ActiveAlarm alarm)
    {
        ActiveAlarm published;
        lock (_sync)
        {
            var existing = _alarms.FirstOrDefault(a =>
                a.Level == alarm.Level && a.Source == alarm.Source && a.Message == alarm.Message);

            if (existing is not null)
            {
                // 同源同内容：合并计数并刷新时间，不新增行
                // 替换对象而非原地修改，旧窗口保持旧现场；诊断缺失时也不能沿用上一次现场。
                var merged = new ActiveAlarm
                {
                    Id = existing.Id, Count = existing.Count + 1, LastAt = DateTime.Now,
                    Level = alarm.Level, Source = alarm.Source, Message = alarm.Message,
                    SystemAction = alarm.SystemAction, ResourceId = alarm.ResourceId,
                    ResourceIsCrane = alarm.ResourceIsCrane, Diagnostic = alarm.Diagnostic
                };
                _alarms[_alarms.IndexOf(existing)] = merged;
                published = merged.CreateSnapshot();
            }
            else
            {
                _alarms.Insert(0, alarm.CreateSnapshot());
                published = alarm.CreateSnapshot();
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return published;
    }

    /// <summary>
    /// 按标识移除一条报警（人工处理后可清除）。
    /// </summary>
    /// <param name="id">报警标识</param>
    /// <returns>移除成功返回 true</returns>
    public bool Remove(Guid id)
    {
        bool removed;
        lock (_sync)
        {
            removed = _alarms.RemoveAll(a => a.Id == id) > 0;
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    /// <summary>
    /// 清空全部活动报警。
    /// </summary>
    public void Clear()
    {
        lock (_sync)
        {
            _alarms.Clear();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 获取报警快照。
    /// </summary>
    /// <returns>报警列表快照</returns>
    public IReadOnlyList<ActiveAlarm> Snapshot()
    {
        lock (_sync)
        {
            return _alarms.Select(alarm => alarm.CreateSnapshot()).ToList();
        }
    }
}
