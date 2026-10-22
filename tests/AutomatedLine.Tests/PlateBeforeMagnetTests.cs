using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Configuration;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Application.Transfer;
using AutomatedLine.Domain.Cranes;
using AutomatedLine.Domain.Workpieces;
using AutomatedLine.Tests.Fakes;

namespace AutomatedLine.Tests;

/// <summary>真实取料原语的补降次数、专用X11反馈、实际位置日志及充磁前失败边界回归。</summary>
public class PlateBeforeMagnetTests
{
    private sealed class Harness
    {
        public FakeCraneDriver Driver { get; } = new();
        public AppOptions Options { get; } = new();
        public TestLogger Logger { get; } = new();
        public TransferContext Context { get; }

        /// <summary>脚本化无板次数与Z坐标，只在相对运动成功后更新实际位置。</summary>
        /// <param name="missingReads">前几次专用X11读取返回无板。</param>
        public Harness(int missingReads)
        {
            Driver.Status = new() { XDisplay = 1000, YDisplay = 10, ZDisplay = 200,
                XEncoder = 2000, YEncoder = 20, ZEncoder = 300, HasPlate = true };
            Driver.OnReadHasPlateAsync = _ => Task.FromResult(Driver.HasPlateReadCount > missingReads);
            Driver.OnRelativeMove = (dx, dy, dz, _) =>
            {
                Assert.Equal(0, dx);
                Assert.Equal(0, dy);
                var previous = Driver.Status!;
                Driver.Status = new() { XDisplay = previous.XDisplay, YDisplay = previous.YDisplay,
                    ZDisplay = previous.ZDisplay + dz, XEncoder = previous.XEncoder,
                    YEncoder = previous.YEncoder, ZEncoder = previous.ZEncoder + dz, HasPlate = true };
                return Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.InPosition, "到位"));
            };
            var drivers = new DriverRegistry();
            drivers.RegisterCrane(Driver);
            Context = new TransferContext(new Crane(1, 1, "天车1"),
                new WorkpieceTask(new WorkpieceInfo { PlateNo = "测试板", SerialNo = "7" }),
                drivers, new FakeConfigurationStore(), Options, Logger);
        }
    }

    /// <summary>原位或允许范围内任一次专用X11有板就停止补降，完整状态的HasPlate不能覆盖专用无板结果。</summary>
    /// <param name="missingReads">成功之前明确无板的读取次数。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Check_StopsAtFirstPlate_AndLogsActualPosition(int missingReads)
    {
        var h = new Harness(missingReads);
        await h.Context.CheckPlateBeforeMagnetAsync(CancellationToken.None);
        Assert.Equal(missingReads + 1, h.Driver.HasPlateReadCount);
        Assert.Equal(missingReads + 1, h.Driver.StatusReadCount);
        Assert.Equal(missingReads, h.Driver.Actions.Count);
        Assert.All(h.Driver.Actions, action => Assert.Equal("REL(0,0,5)", action));
        Assert.False(h.Context.MagnetCommandStarted);
        Assert.False(h.Context.HasPieceOnCrane);
        Assert.Contains(h.Logger.Messages, message => message.Contains("有板检查通过")
            && message.Contains($"已完成补降{missingReads}/3次")
            && message.Contains($"累计指令下降{missingReads * 5}mm")
            && message.Contains($"Z={200 + missingReads * 5}")
            && message.Contains($"Z={300 + missingReads * 5}"));
    }

    /// <summary>默认原位加三次补降仍无板时终止，异常及日志包含次数、累计距离和最后实读坐标。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task Check_ExhaustsThreeDrops_AndReportsFifteenMm()
    {
        var h = new Harness(100);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Context.CheckPlateBeforeMagnetAsync(CancellationToken.None));
        Assert.Equal(4, h.Driver.HasPlateReadCount);
        Assert.Equal(3, h.Driver.Actions.Count);
        Assert.Contains("累计指令下降15mm", failure.Message);
        Assert.Contains("已完成补降3/3次", failure.Message);
        Assert.Contains("Z=215", failure.Message);
        Assert.Contains("尚未充磁", failure.Message);
        Assert.Contains(h.Logger.Messages, message => message.StartsWith("E:StateMachine:")
            && message.Contains("累计指令下降15mm") && message.Contains("Z=315"));
        Assert.False(h.Context.MagnetCommandStarted);
    }

    /// <summary>配置控制追加次数与单步距离；零次数保持原位单次检查，不能偷偷执行默认补降。</summary>
    /// <param name="retryCount">允许的追加下降次数。</param>
    /// <param name="stepMm">每次下降距离。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(0, 9)]
    [InlineData(2, 4)]
    public async Task Check_UsesConfiguredCountAndDistance(int retryCount, int stepMm)
    {
        var h = new Harness(100);
        h.Options.StateMachine.PickPlateRetryCount = retryCount;
        h.Options.StateMachine.PickPlateStepMm = stepMm;
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Context.CheckPlateBeforeMagnetAsync(CancellationToken.None));
        Assert.Equal(retryCount + 1, h.Driver.HasPlateReadCount);
        Assert.Equal(retryCount, h.Driver.Actions.Count);
        Assert.All(h.Driver.Actions, action => Assert.Equal($"REL(0,0,{stepMm})", action));
        Assert.Contains($"累计指令下降{retryCount * stepMm}mm", failure.Message);
    }

    /// <summary>非法次数或距离在读取及动作前失败，防止负距离变成上升或零距离反复空转。</summary>
    /// <param name="retryCount">非法或合法次数。</param>
    /// <param name="stepMm">非法或合法距离。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(-1, 5)]
    [InlineData(3, 0)]
    [InlineData(3, -5)]
    public async Task Check_RejectsInvalidConfigurationBeforeMotion(int retryCount, int stepMm)
    {
        var h = new Harness(0);
        h.Options.StateMachine.PickPlateRetryCount = retryCount;
        h.Options.StateMachine.PickPlateStepMm = stepMm;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Context.CheckPlateBeforeMagnetAsync(CancellationToken.None));
        Assert.Equal(0, h.Driver.HasPlateReadCount);
        Assert.Equal(0, h.Driver.StatusReadCount);
        Assert.Empty(h.Driver.Actions);
    }

    /// <summary>专用X11通信失败立即传播真实异常，不能归为无板继续下降。</summary>
    /// <param name="successfulDrops">通信失败前已完成补降次数。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Check_X11CommunicationFailureDoesNotRetry(int successfulDrops)
    {
        var h = new Harness(100);
        var original = new TimeoutException("X11通信超时");
        h.Driver.OnReadHasPlateAsync = _ => h.Driver.HasPlateReadCount > successfulDrops
            ? Task.FromException<bool>(original) : Task.FromResult(false);
        var failure = await Assert.ThrowsAsync<TimeoutException>(() =>
            h.Context.CheckPlateBeforeMagnetAsync(CancellationToken.None));
        Assert.Same(original, failure);
        Assert.Equal(successfulDrops + 1, h.Driver.HasPlateReadCount);
        Assert.Equal(successfulDrops, h.Driver.Actions.Count);
        Assert.Contains(h.Logger.Messages, message => message.StartsWith("E:StateMachine:")
            && message.Contains("X11通信超时"));
    }

    /// <summary>补降运动失败不继续读取或再次下降，不设置充磁标志。</summary>
    /// <param name="code">模拟到位超时或下压超差。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(CraneCommandCode.Timeout)]
    [InlineData(CraneCommandCode.PressureOutOfRange)]
    public async Task Check_MovementFailureStopsBeforeNextRead(CraneCommandCode code)
    {
        var h = new Harness(100);
        h.Driver.OnRelativeMove = (_, _, _, _) => Task.FromResult(CraneCommandResult.Fail(code, "补降失败"));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Context.CheckPlateBeforeMagnetAsync(CancellationToken.None));
        Assert.Contains(code.ToString(), failure.Message);
        Assert.Equal(1, h.Driver.HasPlateReadCount);
        Assert.Single(h.Driver.Actions);
        Assert.False(h.Context.MagnetCommandStarted);
        Assert.Contains(h.Logger.Messages, message => message.StartsWith("E:StateMachine:")
            && message.Contains("第1/3次补降") && message.Contains("补降失败"));
    }

    /// <summary>补降完成后取消稳定等待，不继续读板或执行下一次下降，保持取消异常类型。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task Check_CancelAfterDropDoesNotReadOrMoveAgain()
    {
        var h = new Harness(100);
        using var cancellation = new CancellationTokenSource();
        h.Driver.OnRelativeMove = (_, _, _, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.InPosition, "到位"));
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            h.Context.CheckPlateBeforeMagnetAsync(cancellation.Token));
        Assert.Equal(1, h.Driver.HasPlateReadCount);
        Assert.Single(h.Driver.Actions);
        Assert.Contains(h.Logger.Messages, message => message.StartsWith("W:StateMachine:")
            && message.Contains("检查中止") && message.Contains("已完成补降1/3次"));
    }

    /// <summary>诊断位置不可用时明确记未知，专用X11仍决定补降和成功，不能捏造零坐标。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task Check_UnavailablePositionIsLoggedAsUnknown()
    {
        var h = new Harness(1);
        h.Driver.OnReadStatus = _ => Task.FromResult<CraneStatusDto?>(null);
        await h.Context.CheckPlateBeforeMagnetAsync(CancellationToken.None);
        Assert.Equal(2, h.Driver.HasPlateReadCount);
        Assert.Single(h.Driver.Actions);
        Assert.Contains(h.Logger.Messages, message => message.Contains("有板检查通过")
            && message.Contains("当前位置未知"));
    }

    /// <summary>位置诊断通信异常明确写日志，但不把它当作专用X11无板或改变取料判据。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task Check_PositionReadExceptionDoesNotReplacePlateDecision()
    {
        var h = new Harness(1);
        h.Driver.OnReadStatus = _ => Task.FromException<CraneStatusDto?>(new TimeoutException("诊断位置超时"));
        await h.Context.CheckPlateBeforeMagnetAsync(CancellationToken.None);
        Assert.Equal(2, h.Driver.HasPlateReadCount);
        Assert.Single(h.Driver.Actions);
        Assert.Contains(h.Logger.Messages, message => message.StartsWith("W:StateMachine:")
            && message.Contains("诊断位置超时"));
        Assert.Contains(h.Logger.Messages, message => message.Contains("有板检查通过")
            && message.Contains("当前位置未知"));
    }

    /// <summary>诊断位置读取阶段的调用者取消仍立即传播，不允许日志读取吞掉中止后继续下降。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task Check_CancelDuringPositionReadDoesNotDescend()
    {
        var h = new Harness(100);
        using var cancellation = new CancellationTokenSource();
        h.Driver.OnReadStatus = token =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<CraneStatusDto?>(token);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            h.Context.CheckPlateBeforeMagnetAsync(cancellation.Token));
        Assert.Equal(1, h.Driver.HasPlateReadCount);
        Assert.Empty(h.Driver.Actions);
        Assert.False(h.Context.MagnetCommandStarted);
    }
}
