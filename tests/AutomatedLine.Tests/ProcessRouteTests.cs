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
}
