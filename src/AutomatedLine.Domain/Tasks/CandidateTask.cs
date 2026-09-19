using AutomatedLine.Domain.Devices;
using AutomatedLine.Domain.Routing;
using AutomatedLine.Domain.Spatial;
using AutomatedLine.Domain.Workpieces;

namespace AutomatedLine.Domain.Tasks;

/// <summary>
/// 候选任务：当前理论上可以执行的一次搬运（架构文档 §8.3）。
/// 它不代表已派发、也不代表天车已开始搬运，只是"当前理论上可以做的事"。
/// 正式任务 <see cref="TransportTask"/> 在派发时由它生成。
/// </summary>
public sealed class CandidateTask
{
    /// <summary>要搬运的工件任务。</summary>
    public WorkpieceTask Workpiece { get; }

    /// <summary>源设备。</summary>
    public DeviceBase Source { get; }

    /// <summary>目标设备。</summary>
    public DeviceBase Target { get; }

    /// <summary>优先级，数值越大越优先。</summary>
    public int Priority { get; }

    /// <summary>目标工序（搬运完成后工件进入的工序）。</summary>
    public ProcessType TargetProcess { get; }

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="workpiece">工件任务</param>
    /// <param name="source">源设备</param>
    /// <param name="target">目标设备</param>
    /// <param name="priority">优先级</param>
    /// <param name="targetProcess">目标工序</param>
    public CandidateTask(WorkpieceTask workpiece, DeviceBase source, DeviceBase target, int priority, ProcessType targetProcess)
    {
        Workpiece = workpiece;
        Source = source;
        Target = target;
        Priority = priority;
        TargetProcess = targetProcess;
    }

    /// <summary>
    /// 计算该候选任务对应的路权区间。
    /// </summary>
    /// <param name="craneWidth">执行天车宽度</param>
    /// <param name="safetyMargin">安全余量</param>
    /// <returns>路权区间</returns>
    public XInterval GetRightOfWayInterval(double craneWidth, double safetyMargin = 0)
        => XInterval.FromTask(Source.PositionX, Target.PositionX, craneWidth, safetyMargin);

    /// <summary>
    /// 便于日志显示的描述。
    /// </summary>
    /// <returns>形如"工件 源→目标"的文本</returns>
    public override string ToString() => $"{Workpiece.Info} {Source.Name}→{Target.Name}";
}
