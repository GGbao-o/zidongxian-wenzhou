namespace AutomatedLine.Domain.Alarms;

/// <summary>
/// 报警级别。用于界面分级展示，也用于区分"瞬时/可重试"与"硬故障"。
/// </summary>
public enum AlarmLevel
{
    /// <summary>提示：正常但不常见的事件。</summary>
    Info = 0,

    /// <summary>警告：瞬时故障（如通信闪断），按重连重试处理，不丢弃任务。</summary>
    Warning = 1,

    /// <summary>错误：硬故障（运动/交互/握手超时等），丢弃任务并暂停线路。</summary>
    Error = 2,

    /// <summary>严重：影响安全的故障，需立即停机检查。</summary>
    Critical = 3
}
