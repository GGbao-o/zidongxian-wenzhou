using AutomatedLine.Domain.Tasks;
using AutomatedLine.Domain.Workpieces;

namespace AutomatedLine.Domain.Cranes;

/// <summary>
/// 天车运行时对象。"一台物理天车 = 一个 Crane 实例"（架构文档 §6.3）。
/// 天车 XYZ 运动由天车 PLC 内部完成，上位机只下发目标与动作并读回结果。
/// </summary>
public sealed class Crane
{
    /// <summary>天车号，与数据库 CraneConfig.Id 对应。</summary>
    public int Id { get; }

    /// <summary>显示序号。</summary>
    public int No { get; set; }

    /// <summary>天车名称，如"天车1"。</summary>
    public string Name { get; set; }

    /// <summary>活动范围左端 X。</summary>
    public double StartX { get; set; }

    /// <summary>活动范围右端 X。</summary>
    public double EndX { get; set; }

    /// <summary>天车物理宽度。用于路权区间向两端外扩半个宽度。</summary>
    public double Width { get; set; }

    /// <summary>安全位 X。任务结束后回到此处，空闲天车不占路权。</summary>
    public double SafeX { get; set; }

    /// <summary>安全位 Z（安全高度）。</summary>
    public double SafeZ { get; set; }

    /// <summary>X 轴偏移量。用于坐标标定/微调。</summary>
    public double OffsetX { get; set; }

    /// <summary>Y 轴偏移量。</summary>
    public double OffsetY { get; set; }

    /// <summary>Z 轴偏移量。</summary>
    public double OffsetZ { get; set; }

    /// <summary>X 轴速度。</summary>
    public double SpeedX { get; set; }

    /// <summary>Y 轴速度。</summary>
    public double SpeedY { get; set; }

    /// <summary>Z 轴速度。</summary>
    public double SpeedZ { get; set; }

    /// <summary>X 轴加速度。</summary>
    public double AccelX { get; set; }

    /// <summary>Y 轴加速度。</summary>
    public double AccelY { get; set; }

    /// <summary>Z 轴加速度。</summary>
    public double AccelZ { get; set; }

    /// <summary>X 轴减速度。</summary>
    public double DecelX { get; set; }

    /// <summary>Y 轴减速度。</summary>
    public double DecelY { get; set; }

    /// <summary>Z 轴减速度。</summary>
    public double DecelZ { get; set; }

    /// <summary>是否启用。停用的天车不参与调度。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 当前工件。只读派生缓存，权威位置是 <see cref="WorkpieceRuntime.CurrentLocation"/>，
    /// 只在取放料确认时由状态机同步。
    /// </summary>
    public WorkpieceTask? CurrentWorkpiece { get; internal set; }

    /// <summary>运行态（当前任务、状态、步骤、持有区间）。</summary>
    public CraneRuntime Runtime { get; } = new();

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="id">天车号</param>
    /// <param name="no">显示序号</param>
    /// <param name="name">名称</param>
    public Crane(int id, int no, string name)
    {
        Id = id;
        No = no;
        Name = name;
    }

    /// <summary>
    /// 判断本车空闲且无任务（只有此时才可能被调度器派发）。
    /// </summary>
    public bool IsIdle => Runtime.State == CraneState.Idle && Runtime.CurrentTask is null;

    /// <summary>
    /// 判断某段 X 是否在本车活动范围内。
    /// </summary>
    /// <param name="x">待判断的 X 坐标</param>
    /// <returns>在范围内返回 true</returns>
    public bool Covers(double x) => x >= StartX && x <= EndX;

    /// <summary>
    /// 便于日志/界面显示。
    /// </summary>
    /// <returns>天车名称</returns>
    public override string ToString() => Name;
}
