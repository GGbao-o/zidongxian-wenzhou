using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Tasks;

namespace AutomatedLine.Tests;

/// <summary>
/// 每车任务队列测试：容量 1，占位时再派发失败。
/// </summary>
public class CraneTaskChannelsTests
{
    /// <summary>同一车队列满（1）时 TryWrite 失败；出队后可再写。</summary>
    [Fact]
    public void Bounded_Capacity_One_Per_Crane()
    {
        var channels = new CraneTaskChannels();
        var channel = channels.For(1);

        Assert.True(channel.Writer.TryWrite(new TransportTask()));
        Assert.False(channel.Writer.TryWrite(new TransportTask()));   // 已占位

        Assert.True(channel.Reader.TryRead(out _));
        Assert.True(channel.Writer.TryWrite(new TransportTask()));    // 出队后可再写
    }

    /// <summary>不同天车队列互相独立。</summary>
    [Fact]
    public void Different_Cranes_Independent()
    {
        var channels = new CraneTaskChannels();

        Assert.True(channels.For(1).Writer.TryWrite(new TransportTask()));
        Assert.True(channels.For(2).Writer.TryWrite(new TransportTask()));   // 车1满不影响车2
    }
}
