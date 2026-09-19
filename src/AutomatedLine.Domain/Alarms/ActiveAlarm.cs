namespace AutomatedLine.Domain.Alarms;

/// <summary>
/// 一条活动报警（内存对象）。
/// 报警本质是异常记录：记录时间、级别、来源、内容与系统动作，并尽量关联到具体资源，
/// 以便报警页做人工处理（清除物理阻塞/释放逻辑锁/补内存）。报警不落库，只写内存与磁盘日志。
/// </summary>
public sealed class ActiveAlarm
{
    /// <summary>报警唯一标识。</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>首次发生时间。</summary>
    public DateTime RaisedAt { get; init; } = DateTime.Now;

    /// <summary>最近一次发生时间（同源同内容会合并计数）。</summary>
    public DateTime LastAt { get; set; } = DateTime.Now;

    /// <summary>级别。</summary>
    public AlarmLevel Level { get; init; }

    /// <summary>来源（如"设备2""天车1""采集器"）。</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>报警内容。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>系统动作（如"重连重试""丢弃任务并暂停线路"）。</summary>
    public string? SystemAction { get; init; }

    /// <summary>关联资源号（设备号或天车号），无关联时为 null。</summary>
    public int? ResourceId { get; init; }

    /// <summary>关联资源是否为天车。</summary>
    public bool ResourceIsCrane { get; init; }

    /// <summary>同源同内容重复发生的次数。</summary>
    public int Count { get; set; } = 1;
}
