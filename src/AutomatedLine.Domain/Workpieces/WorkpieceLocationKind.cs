namespace AutomatedLine.Domain.Workpieces;

/// <summary>
/// 工件位置类别。
/// 与 <see cref="WorkpieceLocation"/> 配合使用：类别决定"在哪类资源上"，
/// 具体资源号由 <see cref="WorkpieceLocation.ResourceId"/> 给出。
/// </summary>
public enum WorkpieceLocationKind
{
    /// <summary>在等待队列（刚录入内存，尚未上线）。</summary>
    Queue = 0,

    /// <summary>位于某台设备上（ResourceId = 设备号）。</summary>
    Device = 1,

    /// <summary>位于某台天车上（ResourceId = 天车号）。</summary>
    Crane = 2,

    /// <summary>已完成下线。</summary>
    Finished = 3,

    /// <summary>已丢弃（异常）。</summary>
    Discarded = 4
}
