namespace AutomatedLine.Domain.Cranes;

/// <summary>
/// 天车运动轴。手动操作/点动时用于指定方向。
/// </summary>
public enum CraneAxis
{
    /// <summary>X 轴（沿产线方向）。</summary>
    X = 0,

    /// <summary>Y 轴。</summary>
    Y = 1,

    /// <summary>Z 轴（升降）。</summary>
    Z = 2
}
