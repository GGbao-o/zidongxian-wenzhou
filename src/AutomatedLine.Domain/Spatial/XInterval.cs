namespace AutomatedLine.Domain.Spatial;

/// <summary>
/// 路权区间值对象（一维 X 轴闭区间）。
/// 一个正在执行的任务 = 一把区间锁；此对象只负责"区间是什么、两个区间是否相交"，
/// 不含任何加锁/解锁逻辑（那是 RightOfWayManager 的职责）。
/// 对应架构文档 §6.6、§8.7.1。
/// </summary>
public readonly record struct XInterval
{
    /// <summary>区间左端（含）。</summary>
    public double Min { get; }

    /// <summary>区间右端（含）。</summary>
    public double Max { get; }

    /// <summary>
    /// 直接由两端坐标构造区间。
    /// 内部自动规整 Min/Max，保证无论调用方顺序如何，区间表示始终一致（Min &lt;= Max）。
    /// </summary>
    /// <param name="min">区间一端 X 坐标</param>
    /// <param name="max">区间另一端 X 坐标</param>
    public XInterval(double min, double max)
    {
        // 允许调用方传入任意顺序，这里统一规整，避免"同一区间两种表示"导致相交判断出错
        Min = Math.Min(min, max);
        Max = Math.Max(min, max);
    }

    /// <summary>
    /// 判断本区间是否与另一区间相交（含端点相接，保守视为相交）。
    /// 用于 RightOfWayManager 的原子 TryAcquire：相交即视为冲突、加锁失败。
    /// </summary>
    /// <param name="other">待比较的另一区间</param>
    /// <returns>相交返回 true；完全分离返回 false</returns>
    public bool Overlaps(XInterval other)
    {
        // 闭区间相交条件：本区间的左端不越过对方的右端，且对方的左端不越过本区间的右端。
        // 端点相接（Min == other.Max）也判定为相交，宁可保守，避免留下半毫米的碰撞隐患。
        return Min <= other.Max && other.Min <= Max;
    }

    /// <summary>
    /// 由任务两端的设备 X 坐标和天车宽度计算路权区间。
    /// 计算规则：两端各外扩半个天车宽度，再各加安全余量（见架构文档 §8.7.1）。
    /// </summary>
    /// <param name="sourceX">源设备 X 坐标</param>
    /// <param name="targetX">目标设备 X 坐标</param>
    /// <param name="craneWidth">天车宽度（来自数据库 CraneConfig.Width）</param>
    /// <param name="safetyMargin">安全余量，默认 0；现场有需要时可调大</param>
    /// <returns>覆盖整个搬运行程的路权区间</returns>
    public static XInterval FromTask(double sourceX, double targetX, double craneWidth, double safetyMargin = 0)
    {
        // 天车有物理宽度，取料/放料时车体中心在设备 X 上，但车体会向两侧各伸出半个宽度，
        // 因此区间两端必须外扩 w/2，否则相邻天车仍可能擦碰
        var halfWidth = craneWidth / 2.0;
        var low = Math.Min(sourceX, targetX) - halfWidth - safetyMargin;
        var high = Math.Max(sourceX, targetX) + halfWidth + safetyMargin;
        return new XInterval(low, high);
    }

    /// <summary>
    /// 便于日志与调试输出的字符串表示，例如 "[4800, 18200]"。
    /// </summary>
    /// <returns>区间字符串</returns>
    public override string ToString() => $"[{Min}, {Max}]";
}
