using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Configuration;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Application.Transfer;
using AutomatedLine.Domain.Cranes;
using AutomatedLine.Domain.Workpieces;
using AutomatedLine.Tests.Fakes;

namespace AutomatedLine.Tests;

/// <summary>源/目标上方只读一次编码器，仅按配置阈值补发一次原相对运动，不追加精度复核。</summary>
public sealed class XyRefinementTests
{
    /// <summary>误差来自编码器；等于阈值跳过，仅超差轴补发，微调结束不再次读取位置。</summary>
    /// <param name="dx">X方向实际编码器与目标之差。</param>
    /// <param name="dy">Y方向实际编码器与目标之差。</param>
    /// <param name="tolerance">本次搬运配置的阈值。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(0, 0, 5)]
    [InlineData(5, -5, 5)]
    [InlineData(-5, 5, 5)]
    [InlineData(6, 0, 5)]
    [InlineData(-6, 0, 5)]
    [InlineData(0, 6, 5)]
    [InlineData(6, -6, 5)]
    [InlineData(6, 0, 10)]
    public async Task Refine_ReadsOnceAndUsesExistingRelativeMove(int dx, int dy, int tolerance)
    {
        var (ctx, driver) = Create();
        ctx.Options.Crane.XyRefineToleranceMm = tolerance;
        driver.Status = new() { XEncoder = 1000 + dx, YEncoder = 2000 + dy, XDisplay = 100, YDisplay = 200 };
        driver.OnRelativeMove = (_, _, _, _) =>
        {
            // 故意保留超差位置：运动自身报告成功就继续，不做第二次编码器判定。
            Assert.Equal(1, driver.StatusReadCount);
            return Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.InPosition, "普通运动完成"));
        };
        await ctx.RefineXyAsync(1000, 2000, default);
        Assert.Equal(1, driver.StatusReadCount);
        var correctX = Math.Abs(dx) > tolerance ? -dx : 0;
        var correctY = Math.Abs(dy) > tolerance ? -dy : 0;
        Assert.Equal(correctX != 0 || correctY != 0
            ? new[] { $"REL({correctX},{correctY},0)" } : Array.Empty<string>(), driver.Actions);
    }

    /// <summary>无法读取状态或报警时不补发；普通微调运动失败沿用原异常处理，不重试。</summary>
    /// <param name="failure">模拟失败阶段。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData("read")]
    [InlineData("alarm")]
    [InlineData("move")]
    public async Task Refine_FailureDoesNotRetry(string failure)
    {
        var (ctx, driver) = Create();
        driver.Status = failure == "read" ? null : new() { XEncoder = 1010, YEncoder = 2000, ServoAlarm = failure == "alarm" ? 1 : 0 };
        driver.OnRelativeMove = (_, _, _, _) => Task.FromResult(CraneCommandResult.Fail(CraneCommandCode.Timeout, "运动超时"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.RefineXyAsync(1000, 2000, default));
        Assert.Equal(1, driver.StatusReadCount);
        Assert.Equal(failure == "move" ? 1 : 0, driver.Actions.Count);
    }

    /// <summary>读取前和读取中取消均传播，避免已有状态接口吞掉取消后继续补发。</summary>
    /// <param name="duringRead">是否在读取状态时发起取消。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refine_CancellationDoesNotMove(bool duringRead)
    {
        var (ctx, driver) = Create();
        using var cancel = new CancellationTokenSource();
        if (!duringRead) cancel.Cancel();
        driver.OnReadStatus = _ => { cancel.Cancel(); return Task.FromResult<CraneStatusDto?>(null); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ctx.RefineXyAsync(1000, 2000, cancel.Token));
        Assert.Equal(duringRead ? 1 : 0, driver.StatusReadCount);
        Assert.Empty(driver.Actions);
    }

    private static (TransferContext Context, FakeCraneDriver Driver) Create()
    {
        var driver = new FakeCraneDriver();
        var drivers = new DriverRegistry();
        drivers.RegisterCrane(driver);
        var options = new AppOptions();
        options.Crane.XyRefineSettleMs = 0;
        return (new TransferContext(new Crane(1, 1, "微调测试"), new WorkpieceTask(new WorkpieceInfo()),
            drivers, new FakeConfigurationStore(), options, new TestLogger()), driver);
    }
}
