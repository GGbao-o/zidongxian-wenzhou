using AutomatedLine.Application.RightOfWay;
using AutomatedLine.Domain.Spatial;

namespace AutomatedLine.Tests;

/// <summary>
/// 路权互斥管理器测试（派发时原子获取、相交即失败）。
/// </summary>
public class RightOfWayManagerTests
{
    /// <summary>无重叠时可获取。</summary>
    [Fact]
    public void TryAcquire_Succeeds_WhenNoOverlap()
    {
        var manager = new RightOfWayManager(new TestLogger());
        var task1 = Guid.NewGuid();

        Assert.True(manager.TryAcquire(task1, new XInterval(0, 1000)));
        Assert.True(manager.IsHeld(task1));
    }

    /// <summary>与已锁区间相交时失败。</summary>
    [Fact]
    public void TryAcquire_Fails_WhenOverlaps()
    {
        var manager = new RightOfWayManager(new TestLogger());
        manager.TryAcquire(Guid.NewGuid(), new XInterval(5000, 18000));

        // 17000 落在已锁区间内 → 相交 → 失败
        Assert.False(manager.TryAcquire(Guid.NewGuid(), new XInterval(17000, 25000)));
    }

    /// <summary>释放后其它任务可重新获取。</summary>
    [Fact]
    public void Release_AllowsReacquire()
    {
        var manager = new RightOfWayManager(new TestLogger());
        var task1 = Guid.NewGuid();
        manager.TryAcquire(task1, new XInterval(0, 1000));

        manager.Release(task1);

        Assert.False(manager.IsHeld(task1));
        Assert.True(manager.TryAcquire(Guid.NewGuid(), new XInterval(0, 1000)));
    }
}
