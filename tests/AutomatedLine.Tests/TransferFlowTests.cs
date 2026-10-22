using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Configuration;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Application.Transfer;
using AutomatedLine.Application.Transfer.Handlers;
using AutomatedLine.Domain.Cranes;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Domain.Workpieces;
using AutomatedLine.Tests.Fakes;

namespace AutomatedLine.Tests;

/// <summary>取放料流程顺序测试（打桩驱动）。</summary>
public class TransferFlowTests
{
    /// <summary>五类源仅在首次到上方后检查XY；斜床保持pickY，失败时禁止下降与充磁。</summary>
    /// <param name="type">源设备类型。</param>
    /// <param name="fail">是否模拟精定位失败。</param>
    /// <returns>异步流程验证任务。</returns>
    [Theory]
    [InlineData(DeviceType.LoadRack, false)]
    [InlineData(DeviceType.BoringMachine, false)]
    [InlineData(DeviceType.LatheMachine, false)]
    [InlineData(DeviceType.GrinderMachine, false)]
    [InlineData(DeviceType.MarkerMachine, false)]
    [InlineData(DeviceType.LoadRack, true)]
    [InlineData(DeviceType.BoringMachine, true)]
    [InlineData(DeviceType.LatheMachine, true)]
    [InlineData(DeviceType.GrinderMachine, true)]
    [InlineData(DeviceType.MarkerMachine, true)]
    public async Task SourceAbove_RefinesBeforeDescentOnly(DeviceType type, bool fail)
    {
        DeviceBase source = type switch
        {
            DeviceType.LoadRack => new LoadRack(1, "上料", 1000),
            DeviceType.BoringMachine => new BoringMachine(1, "双头镗", 1000),
            DeviceType.LatheMachine => new LatheMachine(1, "斜床", 1000),
            DeviceType.GrinderMachine => new GrinderMachine(1, "研磨", 1000),
            _ => new MarkerMachine(1, "打号", 1000)
        };
        ITransferHandler handler = type switch
        {
            DeviceType.LoadRack => new LoadRackHandler(),
            DeviceType.BoringMachine => new BoringHandler(),
            DeviceType.LatheMachine => new LatheHandler(),
            DeviceType.GrinderMachine => new GrinderHandler(),
            _ => new MarkerHandler()
        };
        var wp = new WorkpieceTask(new WorkpieceInfo { Diameter = 100, Length = 700, PlugHole = 100 });
        var ctx = BuildContext(out var driver, out _, out _, source, new UnloadRack(9, "下料", 2000), wp);
        ctx.Options.Transfer.Lathes[1] = new LatheTransferOptions { TailstockCenterDistance = 800, BigHoleOffset = 122 };
        source.Signals = TestSignals.Of(("HasMaterial", true), ("RequestUnload", true), ("ProcessDone", true),
            ("TailstockOpened", true), ("UnloadReleaseDone", true));
        var expectedY = type == DeviceType.LatheMachine ? -172 : 0;
        driver.OnReadStatus = _ =>
        {
            if (driver.StatusReadCount == 1)
            {
                Assert.Equal($"MOVE(1000,{expectedY},500)", Assert.Single(driver.Actions));
                Assert.False(ctx.MagnetCommandStarted);
            }
            return Task.FromResult<CraneStatusDto?>(fail ? null : new()
                { XEncoder = 1000, YEncoder = expectedY, HasPlate = true });
        };
        if (fail)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => handler.PickAsync(ctx, source, default));
            Assert.Single(driver.Actions);
            Assert.False(ctx.MagnetCommandStarted);
        }
        else await handler.PickAsync(ctx, source, default);
        Assert.True(driver.StatusReadCount >= 1);
        Assert.Equal(type == DeviceType.LatheMachine && !fail ? new[] { "REL(0,172,0)" } : Array.Empty<string>(),
            driver.Actions.Where(action => action.StartsWith("REL(")));
    }

    /// <summary>普通绝对运动仍只执行一次；首次上方运动失败也不能进入精定位。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task OrdinaryMovementAndFailedFirstMove_DoNotRefine()
    {
        var source = new LoadRack(1, "上料", 1000);
        source.Signals = TestSignals.Of(("HasMaterial", true));
        var ctx = BuildContext(out var driver, out _, out _, source, new BoringMachine(2, "双头镗", 2000),
            new WorkpieceTask(new WorkpieceInfo { Diameter = 100, Length = 700 }));
        await ctx.MoveAbsoluteAsync(10, 20, 30, default);
        await ctx.MoveAbsoluteAsync(10, 20, 500, default);
        Assert.Equal(0, driver.StatusReadCount);
        driver.FailAbsoluteMoveX = 1000;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new LoadRackHandler().PickAsync(ctx, source, default));
        Assert.Equal(0, driver.StatusReadCount);
    }

    /// <summary>构造流程上下文与打桩驱动。</summary>
    private static TransferContext BuildContext(
        out FakeCraneDriver craneDriver,
        out FakeDeviceDriver sourceDriver,
        out FakeDeviceDriver targetDriver,
        DeviceBase source,
        DeviceBase target,
        WorkpieceTask wp)
    {
        var crane = new Crane(1, 1, "天车1") { SafeZ = 500 };
        var registry = new DriverRegistry();
        craneDriver = new FakeCraneDriver();
        sourceDriver = new FakeDeviceDriver(source.Id);
        targetDriver = new FakeDeviceDriver(target.Id);
        registry.RegisterCrane(craneDriver);
        registry.RegisterDevice(sourceDriver);
        registry.RegisterDevice(targetDriver);

        var store = new FakeConfigurationStore();
        store.Devices.Add(new DeviceConfigDto { Id = source.Id, PositionX = 1000, PositionY = 0, PositionZ = 500 });
        store.Devices.Add(new DeviceConfigDto { Id = target.Id, PositionX = 2000, PositionY = 0, PositionZ = 500 });

        var options = new AppOptions();
        options.Crane.XyRefineSettleMs = 0;
        return new TransferContext(crane, wp, registry, store, options, new TestLogger());
    }

    /// <summary>料架取料：审核有板 → XY → Z 降 → X11 有板 → 充磁 → Z 升。</summary>
    [Fact]
    public async Task LoadRack_Pick_Sequence()
    {
        var source = new LoadRack(1, "上料架", 1000);
        var target = new BoringMachine(2, "双头镗", 2000);
        var wp = new WorkpieceTask(new WorkpieceInfo { Diameter = 100, Length = 700 });
        var ctx = BuildContext(out var craneDriver, out _, out _, source, target, wp);
        source.Signals = TestSignals.Of(("HasMaterial", true));

        await new LoadRackHandler().PickAsync(ctx, source, CancellationToken.None);

        // 料架属"其他设备"，Z 下压 = Round((50/0.9537)+(50/0.866)) = 110 → 500-110 = 390
        Assert.Equal(new[] { "MOVE(1000,0,500)", "MOVE(1000,0,390)", "MAGNET_ON", "MOVE(1000,0,500)" }, craneDriver.Actions);
        Assert.True(ctx.HasPieceOnCrane);
        Assert.True(ctx.MagnetCommandStarted);
    }

    /// <summary>双头镗放料：请求数据 → 参数 → 数据下发完成 → 请求上料 → Z 降 → 放磁 → Z 升 → 上料完成。</summary>
    [Fact]
    public async Task Boring_Place_Writes_Params_And_Done()
    {
        var source = new LoadRack(1, "上料架", 1000);
        var target = new BoringMachine(2, "双头镗", 2000);
        var wp = new WorkpieceTask(new WorkpieceInfo { Diameter = 100, Length = 700 });
        var ctx = BuildContext(out var craneDriver, out _, out var targetDriver, source, target, wp);
        target.Signals = TestSignals.Of(("RequestData", true), ("RequestLoad", true), ("LoadLockDone", true));

        await new BoringHandler().PlaceAsync(ctx, target, CancellationToken.None);

        Assert.Contains("DataSentDone=1", targetDriver.Writes);
        Assert.Contains("LoadDone=1", targetDriver.Writes);
        Assert.Equal("MAGNET_OFF", craneDriver.Actions[^2]);
        Assert.False(ctx.MagnetCommandStarted); // 单独执行放料不会标记取料充磁。
        Assert.Equal(0, craneDriver.StatusReadCount);
    }

    /// <summary>斜床放料：下降后相对-YOffset对接，放磁后保持对接Y回升，不返回中心Y。</summary>
    [Fact]
    public async Task Lathe_Place_Moves_Relative_By_YOffset()
    {
        var source = new LoadRack(1, "上料架", 1000);
        var target = new LatheMachine(5, "斜床01", 3000);
        var wp = new WorkpieceTask(new WorkpieceInfo { Diameter = 100, Length = 700, PlugHole = 100 });
        var ctx = BuildContext(out var craneDriver, out _, out var targetDriver, source, target, wp);
        // 每台斜床一份参数：顶尖距离 800、大孔 122 → YOffset = (800-700)/2 + 122 = 172
        ctx.Options.Transfer.Lathes[5] = new LatheTransferOptions { TailstockCenterDistance = 800, BigHoleOffset = 122, SmallHoleOffset = 55 };
        target.Signals = TestSignals.Of(("RequestData", true), ("RequestLoad", true), ("TailstockClamped", true));

        await new LatheHandler().PlaceAsync(ctx, target, CancellationToken.None);

        Assert.Contains("REL(0,-172,0)", craneDriver.Actions);
        Assert.Equal("MAGNET_OFF", craneDriver.Actions[^2]);
        Assert.Equal("MOVE(2000,-172,500)", craneDriver.Actions[^1]);
        Assert.Contains("CraneLoadInPlace=1", targetDriver.Writes);   // #1102 天车上料到位
        Assert.Contains("CraneLoadDone=1", targetDriver.Writes);
        Assert.Equal(0, craneDriver.StatusReadCount);
    }

    /// <summary>下料架放料：Z回安全高度后发完成脉冲，保持1秒并复位。</summary>
    /// <returns>异步流程验证任务。</returns>
    [Fact]
    public async Task UnloadRack_Place_PulsesDoneAfterSafeRise()
    {
        var source = new GrinderMachine(6, "研磨机", 6000);
        var target = new UnloadRack(9, "下料架", 9000);
        var wp = new WorkpieceTask(new WorkpieceInfo { Diameter = 100, Length = 700 });
        var ctx = BuildContext(out var craneDriver, out _, out var targetDriver, source, target, wp);
        target.Signals = TestSignals.Of(("HasMaterial", false));

        await new UnloadRackHandler().PlaceAsync(ctx, target, CancellationToken.None);

        Assert.Contains("MAGNET_OFF", craneDriver.Actions);
        Assert.Equal(new[] { "PlaceDone=1", "PlaceDone=0" }, targetDriver.Writes);
        Assert.Equal(0, craneDriver.StatusReadCount);
    }
}
