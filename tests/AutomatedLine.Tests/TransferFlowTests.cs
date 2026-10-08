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

        return new TransferContext(crane, wp, registry, store, new AppOptions(), new TestLogger());
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
        target.Signals = TestSignals.Of(("RequestData", true), ("RequestLoad", true));

        await new BoringHandler().PlaceAsync(ctx, target, CancellationToken.None);

        Assert.Contains("DataSentDone=1", targetDriver.Writes);
        Assert.Contains("LoadDone=1", targetDriver.Writes);
        Assert.Equal("MAGNET_OFF", craneDriver.Actions[^2]);
        Assert.False(ctx.MagnetCommandStarted); // 单独执行放料不会标记取料充磁。
    }

    /// <summary>斜床放料：Z 降后相对 -YOffset，再放磁。</summary>
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
        Assert.Contains("CraneLoadInPlace=1", targetDriver.Writes);   // #1102 天车上料到位
        Assert.Contains("CraneLoadDone=1", targetDriver.Writes);
    }

    /// <summary>下料架放料：无需参数/请求上料，只放磁。</summary>
    [Fact]
    public async Task UnloadRack_Place_Only_MagnetOff()
    {
        var source = new GrinderMachine(6, "研磨机", 6000);
        var target = new UnloadRack(9, "下料架", 9000);
        var wp = new WorkpieceTask(new WorkpieceInfo { Diameter = 100, Length = 700 });
        var ctx = BuildContext(out var craneDriver, out _, out var targetDriver, source, target, wp);
        target.Signals = TestSignals.Of(("HasMaterial", false));

        await new UnloadRackHandler().PlaceAsync(ctx, target, CancellationToken.None);

        Assert.Contains("MAGNET_OFF", craneDriver.Actions);
        Assert.Empty(targetDriver.Writes);   // 无参数、无上料完成
    }
}
