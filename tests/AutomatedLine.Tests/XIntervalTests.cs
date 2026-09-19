using AutomatedLine.Domain.Spatial;

namespace AutomatedLine.Tests;

/// <summary>
/// 路权区间值对象测试。
/// </summary>
public class XIntervalTests
{
    /// <summary>相交区间应判定为 Overlaps。</summary>
    [Fact]
    public void Overlaps_WhenRangesIntersect_ReturnsTrue()
    {
        var a = new XInterval(0, 100);
        var b = new XInterval(80, 200);

        Assert.True(a.Overlaps(b));
        Assert.True(b.Overlaps(a));
    }

    /// <summary>完全分离的区间不应相交。</summary>
    [Fact]
    public void Overlaps_WhenDisjoint_ReturnsFalse()
    {
        var a = new XInterval(0, 100);
        var b = new XInterval(101, 200);

        Assert.False(a.Overlaps(b));
    }

    /// <summary>端点相接（闭区间）保守判定为相交。</summary>
    [Fact]
    public void Overlaps_WhenTouching_ReturnsTrue()
    {
        var a = new XInterval(0, 100);
        var b = new XInterval(100, 200);

        Assert.True(a.Overlaps(b));
    }

    /// <summary>由任务计算区间时，两端各外扩半个车宽并加安全余量。</summary>
    [Fact]
    public void FromTask_ExpandsByHalfWidthAndMargin()
    {
        var interval = XInterval.FromTask(sourceX: 5000, targetX: 18000, craneWidth: 800, safetyMargin: 100);

        Assert.Equal(5000 - 400 - 100, interval.Min, 3);
        Assert.Equal(18000 + 400 + 100, interval.Max, 3);
    }

    /// <summary>构造时自动规整 Min/Max 顺序。</summary>
    [Fact]
    public void Constructor_NormalizesOrder()
    {
        var interval = new XInterval(200, 100);

        Assert.Equal(100, interval.Min);
        Assert.Equal(200, interval.Max);
    }
}
