using AutomatedLine.Application.Abstractions;
using AutomatedLine.Domain.Alarms;
using AutomatedLine.Domain.Diagnostics;

namespace AutomatedLine.Application.Alarms;

/// <summary>
/// 报警管理器（横切）：任何模块发现异常都可调用它。
/// 报警本质是异常日志：这里同时写入内存活动报警（供页面秒级展示）与磁盘日志（alarm.txt，供事后追溯）。
/// 按架构文档 §8.8、§13.7。
/// </summary>
public sealed class AlarmManager
{
    /// <summary>日志。</summary>
    private readonly IAppLogger _logger;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="store">活动报警存储</param>
    /// <param name="logger">日志</param>
    public AlarmManager(ActiveAlarmStore store, IAppLogger logger)
    {
        Store = store;
        _logger = logger;
    }

    /// <summary>活动报警存储，供报警页直接读取。</summary>
    public ActiveAlarmStore Store { get; }

    /// <summary>
    /// 触发一条报警。
    /// </summary>
    /// <param name="level">级别</param>
    /// <param name="source">来源</param>
    /// <param name="message">内容</param>
    /// <param name="systemAction">系统动作说明，可为空</param>
    /// <param name="resourceId">关联资源号，可为空</param>
    /// <param name="resourceIsCrane">关联资源是否天车</param>
    public void Raise(
        AlarmLevel level,
        string source,
        string message,
        string? systemAction = null,
        int? resourceId = null,
        bool resourceIsCrane = false)
    {
        var alarm = new ActiveAlarm
        {
            Level = level,
            Source = source,
            Message = message,
            SystemAction = systemAction,
            ResourceId = resourceId,
            ResourceIsCrane = resourceIsCrane
        };

        Store.Raise(alarm);

        // Error 及以上写错误日志，其余写警告；都归到 Alarm 类别，便于按天查阅
        var text = systemAction is null ? $"{source} {message}" : $"{source} {message}（系统动作：{systemAction}）";
        if (level >= AlarmLevel.Error)
        {
            _logger.Error(LogCategory.Alarm, text);
        }
        else
        {
            _logger.Warn(LogCategory.Alarm, text);
        }
    }
}
