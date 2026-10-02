using AutomatedLine.Application.Reservations;

namespace AutomatedLine.Tests;

/// <summary>
/// 设备预约管理器测试。
/// </summary>
public class ResourceReservationManagerTests
{
    /// <summary>正常预约源与目标设备。</summary>
    [Fact]
    public void TryReserve_Succeeds_ForFreeDevices()
    {
        var manager = new ResourceReservationManager(new TestLogger());
        var task = Guid.NewGuid();

        Assert.True(manager.TryReserve(task, sourceId: 2, targetId: 3));
        Assert.True(manager.IsReserved(2));
        Assert.True(manager.IsReserved(3));
    }

    /// <summary>目标已被预约时整体失败，不部分占用。</summary>
    [Fact]
    public void TryReserve_Fails_WhenDeviceAlreadyReserved()
    {
        var manager = new ResourceReservationManager(new TestLogger());
        manager.TryReserve(Guid.NewGuid(), 2, 3);

        Assert.False(manager.TryReserve(Guid.NewGuid(), 4, 3));
        Assert.False(manager.IsReserved(4));
    }

    /// <summary>释放后设备可再次被预约。</summary>
    [Fact]
    public void Release_FreesDevices()
    {
        var manager = new ResourceReservationManager(new TestLogger());
        var task = Guid.NewGuid();
        manager.TryReserve(task, 2, 3);

        manager.Release(task);

        Assert.False(manager.IsReserved(2));
        Assert.True(manager.TryReserve(Guid.NewGuid(), 2, 3));
    }

    /// <summary>源与目标相同应失败。</summary>
    [Fact]
    public void TryReserve_Fails_WhenSourceEqualsTarget()
    {
        var manager = new ResourceReservationManager(new TestLogger());

        Assert.False(manager.TryReserve(Guid.NewGuid(), 5, 5));
    }

    /// <summary>只释放源设备，目标仍被预约。</summary>
    [Fact]
    public void ReleaseSource_Frees_OnlySource()
    {
        var manager = new ResourceReservationManager(new TestLogger());
        var task = Guid.NewGuid();
        manager.TryReserve(task, sourceId: 2, targetId: 3);

        Assert.True(manager.ReleaseSource(task));
        Assert.False(manager.IsReserved(2));   // 源已释放
        Assert.True(manager.IsReserved(3));    // 目标仍占用
        Assert.True(manager.TryReserve(Guid.NewGuid(), 2, 9));   // 源可被别的任务预约
    }

    /// <summary>源已释放后再 ReleaseSource 返回 false（幂等）。</summary>
    [Fact]
    public void ReleaseSource_SecondCall_ReturnsFalse()
    {
        var manager = new ResourceReservationManager(new TestLogger());
        var task = Guid.NewGuid();
        manager.TryReserve(task, 2, 3);

        Assert.True(manager.ReleaseSource(task));
        Assert.False(manager.ReleaseSource(task));
    }

    /// <summary>ReleaseSource 后整体 Release 仍能释放目标。</summary>
    [Fact]
    public void Release_AfterReleaseSource_FreesTarget()
    {
        var manager = new ResourceReservationManager(new TestLogger());
        var task = Guid.NewGuid();
        manager.TryReserve(task, 2, 3);

        manager.ReleaseSource(task);
        Assert.True(manager.Release(task));
        Assert.False(manager.IsReserved(3));
    }

    /// <summary>
    /// 源已 ReleaseSource 后又被别的任务预约：中止本任务**不得**释放别人的源。
    /// （状态机执行到 Transit 会释放源，之后源可能被别的天车预约。）
    /// </summary>
    [Fact]
    public void Release_AfterSourceReReserved_DoesNotFreeOtherSource()
    {
        var manager = new ResourceReservationManager(new TestLogger());
        var task = Guid.NewGuid();
        manager.TryReserve(task, sourceId: 2, targetId: 3);
        manager.ReleaseSource(task);                 // 源2 已释放、变空闲

        var other = Guid.NewGuid();
        Assert.True(manager.TryReserve(other, 2, 9)); // 别的任务预约了源2

        manager.Release(task);                        // 中止本任务
        Assert.True(manager.IsReserved(2));           // 源2 仍属别的任务
        Assert.False(manager.IsReserved(3));          // 本任务目标已释放
    }
}
