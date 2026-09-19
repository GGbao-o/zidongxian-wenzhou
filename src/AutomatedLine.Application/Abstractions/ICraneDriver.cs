namespace AutomatedLine.Application.Abstractions;

/// <summary>
/// 天车通信驱动。
/// 负责写目标位置与动作指令、读天车 X/Y/Z 坐标、到位、报警、伴信号等。
/// 天车三轴运动由天车 PLC 内部完成，上位机只负责下发目标与动作（架构文档 §2.1）。
/// 具体点位定义在天车代码类中。
/// </summary>
public interface ICraneDriver : IPlcDriver
{
    /// <summary>天车号，与 CraneConfig.Id 对应。</summary>
    int CraneId { get; }
}
