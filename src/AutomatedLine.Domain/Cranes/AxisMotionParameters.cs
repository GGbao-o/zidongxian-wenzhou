namespace AutomatedLine.Domain.Cranes;

/// <summary>
/// 单轴运动参数：速度、加速度、减速度。
/// 手动操作页为 X/Y/Z 三轴分别设置这些参数。
/// </summary>
/// <param name="Speed">速度</param>
/// <param name="Accel">加速度</param>
/// <param name="Decel">减速度</param>
public readonly record struct AxisMotionParameters(double Speed, double Accel, double Decel);

/// <summary>
/// 三轴运动参数集合。
/// </summary>
/// <param name="X">X 轴参数</param>
/// <param name="Y">Y 轴参数</param>
/// <param name="Z">Z 轴参数</param>
public readonly record struct TripleAxisMotionParameters(
    AxisMotionParameters X,
    AxisMotionParameters Y,
    AxisMotionParameters Z);
