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

    /// <summary>Resize 把区间缩小，成功且仍持有。</summary>
    [Fact]
    public void Resize_Shrinks_HeldInterval()
    {
        var manager = new RightOfWayManager(new TestLogger());
        var task = Guid.NewGuid();
        manager.TryAcquire(task, new XInterval(5000, 18000));

        Assert.True(manager.Resize(task, new XInterval(9000, 11000)));
        Assert.True(manager.IsHeld(task));
        // 缩小后，原区间两端现在空闲：可被别的任务获取
        Assert.True(manager.TryAcquire(Guid.NewGuid(), new XInterval(5000, 8000)));
    }

    /// <summary>Resize 与其它任务的锁相交时失败，且保留原区间。</summary>
    [Fact]
    public void Resize_Fails_WhenOverlaps_Other()
    {
        var manager = new RightOfWayManager(new TestLogger());
        var task1 = Guid.NewGuid();
        var task2 = Guid.NewGuid();
        manager.TryAcquire(task1, new XInterval(0, 1000));
        manager.TryAcquire(task2, new XInterval(5000, 6000));

        // 想把 task1 扩到与 task2 相交 → 失败
        Assert.False(manager.Resize(task1, new XInterval(0, 5500)));

        // 原区间仍被持有（别的任务无法拿下 0~1000）
        Assert.False(manager.TryAcquire(Guid.NewGuid(), new XInterval(0, 1000)));
    }

    /// <summary>未持有路权时 Resize 返回 false。</summary>
    [Fact]
    public void Resize_Fails_WhenNotHeld()
    {
        var manager = new RightOfWayManager(new TestLogger());
        Assert.False(manager.Resize(Guid.NewGuid(), new XInterval(0, 10)));
    }
}
