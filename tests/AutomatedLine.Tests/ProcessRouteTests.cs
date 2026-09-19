using AutomatedLine.Domain.Devices;
using AutomatedLine.Domain.Routing;

namespace AutomatedLine.Tests;

/// <summary>
/// 工艺路线测试。
/// </summary>
public class ProcessRouteTests
{
    /// <summary>下一工序按内置顺序返回。</summary>
    [Fact]
    public void GetNext_ReturnsFollowingProcess()
    {
        var route = ProcessRoute.CreateDefault();

        Assert.Equal(ProcessType.Boring, route.GetNext(ProcessType.Loading)!.Process);
        Assert.Equal(ProcessType.Lathing, route.GetNext(ProcessType.Boring)!.Process);
    }

    /// <summary>最后一道工序之后没有下一道。</summary>
    [Fact]
    public void GetNext_AtLastProcess_ReturnsNull()
    {
        var route = ProcessRoute.CreateDefault();

        Assert.Null(route.GetNext(ProcessType.Unloading));
        Assert.True(route.IsLast(ProcessType.Unloading));
    }

    /// <summary>未定义工序返回 null。</summary>
    [Fact]
    public void GetNext_UnknownProcess_ReturnsNull()
    {
        var route = ProcessRoute.CreateDefault();

        Assert.Null(route.GetNext(ProcessType.Unknown));
    }

    /// <summary>可选工序缺设备时应被跳过（打号/验点无设备 → 动平衡直接到下料）。</summary>
    [Fact]
    public void GetNextAvailable_SkipsOptionalWithoutDevice()
    {
        var route = ProcessRoute.CreateDefault();

        // 只有上料/双头镗/斜床/研磨/动平衡/下料这些必需品，没有打号与验点
        bool HasDevice(DeviceType type) => type is not (DeviceType.MarkerMachine or DeviceType.CheckMachine);

        var next = route.GetNextAvailable(ProcessType.Balancing, HasDevice);

        Assert.NotNull(next);
        Assert.Equal(ProcessType.Unloading, next!.Process);
    }

    /// <summary>可选工序有设备时应正常返回。</summary>
    [Fact]
    public void GetNextAvailable_ReturnsOptionalWhenDeviceExists()
    {
        var route = ProcessRoute.CreateDefault();

        var next = route.GetNextAvailable(ProcessType.Balancing, _ => true);

        Assert.NotNull(next);
        Assert.Equal(ProcessType.Marking, next!.Process);
    }
}
