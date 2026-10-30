namespace AutomatedLine.Domain.Alarms;

/// <summary>
/// 一条活动报警（内存对象）。
/// 报警本质是异常记录：记录时间、级别、来源、内容与系统动作，并尽量关联到具体资源，
/// 供报警页只读展示。报警窗口不执行清理、释放锁或恢复线路；报警不落库，只写内存与磁盘日志。
/// </summary>
public sealed class ActiveAlarm
{
    /// <summary>报警唯一标识。</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

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

    /// <summary>最近一次故障的只读诊断；普通提示或旧报警可能没有。</summary>
    public AlarmDiagnostic? Diagnostic { get; init; }

    /// <summary>复制显示值，避免列表合并、资源清理或后续报警改写已经打开的详情。</summary>
    /// <returns>标识及诊断内容相同的独立报警对象。</returns>
    public ActiveAlarm CreateSnapshot() => new()
    {
        Id = Id, LastAt = LastAt, Level = Level, Source = Source, Message = Message,
        SystemAction = SystemAction, ResourceId = ResourceId, ResourceIsCrane = ResourceIsCrane,
        Count = Count, Diagnostic = Diagnostic
    };
}
