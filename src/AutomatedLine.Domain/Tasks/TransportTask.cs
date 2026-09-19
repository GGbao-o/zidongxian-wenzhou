using AutomatedLine.Domain.Routing;
using AutomatedLine.Domain.Spatial;

namespace AutomatedLine.Domain.Tasks;

/// <summary>
/// 搬运任务：一次"从源设备搬到目标设备"的正式任务。
/// 一旦派发就归属于某台天车（架构文档 §17 不变式 3）。
/// 路权区间由源/目标 X 与天车宽度算出，派发时一次性锁定（架构文档 §8.4、§8.7.1）。
/// </summary>
public sealed class TransportTask
{
    /// <summary>任务唯一标识，内存生成。</summary>
    public Guid TaskId { get; init; } = Guid.NewGuid();

    /// <summary>关联的工件运行期标识，对应 <see cref="Workpieces.WorkpieceInfo.RuntimeId"/>。</summary>
    public Guid WorkpieceRuntimeId { get; init; }

    /// <summary>源设备号。</summary>
    public int SourceDeviceId { get; init; }

    /// <summary>目标设备号。</summary>
    public int TargetDeviceId { get; init; }

    /// <summary>源设备 X 坐标，用于计算路权区间。</summary>
    public double SourceX { get; init; }

    /// <summary>目标设备 X 坐标，用于计算路权区间。</summary>
    public double TargetX { get; init; }

    /// <summary>搬运完成后工件进入的工序。放料确认时写入工件运行态。</summary>
    public ProcessType TargetProcess { get; init; }

    /// <summary>优先级。数值越大越优先。</summary>
    public int Priority { get; init; }

    /// <summary>任务状态。</summary>
    public TransportTaskState State { get; set; } = TransportTaskState.Created;

    /// <summary>被派发到的天车号；未派发时为 null。</summary>
    public int? AssignedCraneId { get; set; }

    /// <summary>
    /// 计算本任务对应的路权区间。
    /// 抽成方法而不是存字段，保证"区间永远由当前源/目标坐标与车宽算出"，不会与配置漂移。
    /// </summary>
    /// <param name="craneWidth">执行该任务的天车宽度</param>
    /// <param name="safetyMargin">安全余量</param>
    /// <returns>路权区间</returns>
    public XInterval GetRightOfWayInterval(double craneWidth, double safetyMargin = 0)
        => XInterval.FromTask(SourceX, TargetX, craneWidth, safetyMargin);
}
