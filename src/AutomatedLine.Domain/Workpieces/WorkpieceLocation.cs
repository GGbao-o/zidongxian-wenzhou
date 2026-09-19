namespace AutomatedLine.Domain.Workpieces;

/// <summary>
/// 工件位置值对象。
/// 这是全系统唯一的"位置事实来源"（架构文档 §6.1、§17 不变式 1）：
/// 设备/天车的 CurrentWorkpiece 都只是查询派生的结果，不允许各自独立写位置。
/// 变化只允许由天车状态机在"取放料确认成功"那一刻单点写入。
/// </summary>
public readonly record struct WorkpieceLocation
{
    /// <summary>位置类别。</summary>
    public WorkpieceLocationKind Kind { get; }

    /// <summary>资源号：位于设备/天车时为对应 Id；队列/完成/丢弃时为 null。</summary>
    public int? ResourceId { get; }

    /// <summary>
    /// 私有构造。外部一律通过静态工厂创建，避免出现"Device 却没有资源号"这类非法组合。
    /// </summary>
    /// <param name="kind">位置类别</param>
    /// <param name="resourceId">资源号</param>
    private WorkpieceLocation(WorkpieceLocationKind kind, int? resourceId)
    {
        Kind = kind;
        ResourceId = resourceId;
    }

    /// <summary>在等待队列。</summary>
    public static WorkpieceLocation Queue => new(WorkpieceLocationKind.Queue, null);

    /// <summary>已完成下线。</summary>
    public static WorkpieceLocation Finished => new(WorkpieceLocationKind.Finished, null);

    /// <summary>已丢弃。</summary>
    public static WorkpieceLocation Discarded => new(WorkpieceLocationKind.Discarded, null);

    /// <summary>
    /// 构造"位于设备上"的位置。
    /// </summary>
    /// <param name="deviceId">设备号</param>
    /// <returns>设备位置</returns>
    public static WorkpieceLocation AtDevice(int deviceId) => new(WorkpieceLocationKind.Device, deviceId);

    /// <summary>
    /// 构造"位于天车上"的位置。
    /// </summary>
    /// <param name="craneId">天车号</param>
    /// <returns>天车位置</returns>
    public static WorkpieceLocation OnCrane(int craneId) => new(WorkpieceLocationKind.Crane, craneId);

    /// <summary>是否位于设备上。</summary>
    public bool IsOnDevice => Kind == WorkpieceLocationKind.Device;

    /// <summary>是否位于天车上。</summary>
    public bool IsOnCrane => Kind == WorkpieceLocationKind.Crane;

    /// <summary>
    /// 便于日志与界面显示的位置文本，例如"设备(3)""天车(1)""队列"。
    /// </summary>
    /// <returns>位置文本</returns>
    public override string ToString() => Kind switch
    {
        WorkpieceLocationKind.Device => $"设备({ResourceId})",
        WorkpieceLocationKind.Crane => $"天车({ResourceId})",
        WorkpieceLocationKind.Queue => "队列",
        WorkpieceLocationKind.Finished => "已完成",
        WorkpieceLocationKind.Discarded => "已丢弃",
        _ => "未知"
    };
}
