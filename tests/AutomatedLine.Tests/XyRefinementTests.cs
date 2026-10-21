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
    [InlineData(200, 100, 5)]
    [InlineData(-200, -100, 5)]
    [InlineData(200, -100, 5)]
    [InlineData(-200, 100, 5)]
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

    /// <summary>任一轴正负误差超限都禁止整次微调，不截断距离，也不能先移动另一条合法轴。</summary>
    /// <param name="dx">编码器相对目标的X偏差。</param>
    /// <param name="dy">编码器相对目标的Y偏差。</param>
    /// <param name="axes">异常应标记的超限轴。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(201, 6, "X")]
    [InlineData(-201, 6, "X")]
    [InlineData(6, 101, "Y")]
    [InlineData(6, -101, "Y")]
    [InlineData(201, -101, "X、Y")]
    public async Task Refine_AnyAxisOverLimitRejectsEntireMove(int dx, int dy, string axes)
    {
        var logger = new TestLogger();
        var (ctx, driver) = Create(logger);
        driver.Status = new() { XEncoder = 1000 + dx, YEncoder = 2000 + dy };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.RefineXyAsync(1000, 2000, default));
        Assert.Contains($"超限轴={axes}，", error.Message);
        Assert.Contains($"差值dX={-dx} dY={-dy}mm", error.Message);
        Assert.Contains("最大距离X=200 Y=100mm", error.Message);
        Assert.Contains("整次未下发微调", error.Message);
        Assert.Contains(logger.Messages, m => m.StartsWith("E:StateMachine:") && m.Contains("XY微调超限"));
        Assert.Equal(1, driver.StatusReadCount);
        Assert.Empty(driver.Actions);
    }

    /// <summary>上限按本次上下文配置生效，等于自定义边界允许，大于任一边界均不下发。</summary>
    /// <param name="dx">X编码器偏差。</param>
    /// <param name="dy">Y编码器偏差。</param>
    /// <param name="allowed">是否应允许一次相对微调。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(20, -10, true)]
    [InlineData(21, -10, false)]
    [InlineData(20, -11, false)]
    public async Task Refine_CustomLimitsAreUsed(int dx, int dy, bool allowed)
    {
        var (ctx, driver) = Create();
        ctx.Options.Crane.XyRefineMaxXCorrectionMm = 20;
        ctx.Options.Crane.XyRefineMaxYCorrectionMm = 10;
        driver.Status = new() { XEncoder = 1000 + dx, YEncoder = 2000 + dy };
        if (allowed)
        {
            await ctx.RefineXyAsync(1000, 2000, default);
            Assert.Equal($"REL({-dx},{-dy},0)", Assert.Single(driver.Actions));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.RefineXyAsync(1000, 2000, default));
            Assert.Empty(driver.Actions);
        }
    }

    /// <summary>极端编码器差值先以long判超限，不能溢出、截断或发送意外反向运动。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task Refine_LargeEncoderErrorReportsLimitInsteadOfOverflow()
    {
        var (ctx, driver) = Create();
        driver.Status = new() { XEncoder = int.MinValue, YEncoder = 2000 };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.RefineXyAsync(1000, 2000, default));
        Assert.Contains("差值dX=2147484648", error.Message);
        Assert.Contains("XY微调超限", error.Message);
        Assert.Empty(driver.Actions);
    }

    /// <summary>配置非法时在等待/读取/下发之前拒绝，避免不一致的上下限悄悄放行。</summary>
    /// <param name="maxX">X配置上限。</param>
    /// <param name="maxY">Y配置上限。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(0, 100)]
    [InlineData(-1, 100)]
    [InlineData(200, 0)]
    [InlineData(200, -1)]
    [InlineData(4, 100)]
    [InlineData(200, 4)]
    public async Task Refine_InvalidLimitsRejectBeforeReading(int maxX, int maxY)
    {
        var (ctx, driver) = Create();
        ctx.Options.Crane.XyRefineMaxXCorrectionMm = maxX;
        ctx.Options.Crane.XyRefineMaxYCorrectionMm = maxY;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.RefineXyAsync(1000, 2000, default));
        Assert.Contains("XY微调参数非法", error.Message);
        Assert.Equal(0, driver.StatusReadCount);
        Assert.Empty(driver.Actions);
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

    private static (TransferContext Context, FakeCraneDriver Driver) Create(TestLogger? logger = null)
    {
        var driver = new FakeCraneDriver();
        var drivers = new DriverRegistry();
        drivers.RegisterCrane(driver);
        var options = new AppOptions();
        options.Crane.XyRefineSettleMs = 0;
        return (new TransferContext(new Crane(1, 1, "微调测试"), new WorkpieceTask(new WorkpieceInfo()),
            drivers, new FakeConfigurationStore(), options, logger ?? new TestLogger()), driver);
    }
}
