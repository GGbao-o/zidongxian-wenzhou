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
}
