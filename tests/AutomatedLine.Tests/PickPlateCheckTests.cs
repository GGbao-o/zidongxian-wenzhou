using System.Diagnostics;
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

/// <summary>验证各源设备在原位和每次补降后等待稳定，专用X11确认成功前不充磁、不写后续握手。</summary>
public class PickPlateCheckTests
{
    /// <summary>五种来源原位有板即可充磁，无板至补降耗尽/通信失败不能充磁或进行后续握手。</summary>
    /// <param name="sourceKind">源设备类型序号</param>
    /// <param name="result">有板、无板或读取失败</param>
    /// <returns>异步验证任务</returns>
    [Theory]
    [InlineData(0, "plate")]
    [InlineData(1, "plate")]
    [InlineData(2, "plate")]
    [InlineData(3, "plate")]
    [InlineData(4, "plate")]
    [InlineData(0, "empty")]
    [InlineData(1, "empty")]
    [InlineData(2, "empty")]
    [InlineData(3, "empty")]
    [InlineData(4, "empty")]
    [InlineData(0, "communication")]
    [InlineData(1, "communication")]
    [InlineData(2, "communication")]
    [InlineData(3, "communication")]
    [InlineData(4, "communication")]
    public async Task PickChecksAfterEachSettlingBeforeMagnet(int sourceKind, string result)
    {
        (DeviceBase Source, ITransferHandler Handler) selection = sourceKind switch
        {
            0 => (new LoadRack(1, "上料架", 1000), new LoadRackHandler()),
            1 => (new BoringMachine(1, "双头镗", 1000), new BoringHandler()),
            2 => (new LatheMachine(1, "斜床", 1000), new LatheHandler()),
            3 => (new GrinderMachine(1, "研磨", 1000), new GrinderHandler()),
            _ => (new MarkerMachine(1, "打号", 1000), new MarkerHandler())
        };
        var (source, handler) = selection;
        source.Signals = TestSignals.Of(("HasMaterial", true), ("RequestUnload", true),
            ("ProcessDone", true), ("TailstockOpened", true), ("UnloadReleaseDone", true));
        var crane = new Crane(1, 1, "天车1") { SafeZ = 500 };
        var driver = new FakeCraneDriver
        {
            Status = new() { HasPlate = result == "plate" },
            HasPlateException = result == "communication" ? new TimeoutException("X11 读取超时") : null
        };
        var deviceDriver = new FakeDeviceDriver(1);
        var drivers = new DriverRegistry();
        drivers.RegisterCrane(driver);
        drivers.RegisterDevice(deviceDriver);
        var store = new FakeConfigurationStore();
        store.Devices.Add(new DeviceConfigDto { Id = 1, PositionX = 1000, PositionY = 0, PositionZ = 500 });
        var options = new AppOptions();
        // 本组只验证下降后的X11等待；关闭独立XY读取前延时，保留原两次MOVE后的观察边界。
        options.Crane.XyRefineSettleMs = 0;
        options.Transfer.Lathes[1] = new LatheTransferOptions { TailstockCenterDistance = 800 };
        var ctx = new TransferContext(crane, new WorkpieceTask(new WorkpieceInfo { Diameter = 100, Length = 700 }),
            drivers, store, options, new TestLogger());
        var reads = 0;
        var elapsed = Stopwatch.StartNew();
        driver.OnRelativeMove = (dx, dy, dz, _) =>
        {
            if (dz > 0)
            {
                Assert.Equal(0, dx);
                Assert.Equal(0, dy);
                Assert.Equal(options.StateMachine.PickPlateStepMm, dz);
                elapsed.Restart(); // 每次补降后也必须重新等待稳定，不能只在原位等一次。
            }
            return Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.InPosition, "到位"));
        };
        driver.OnReadHasPlate = () =>
        {
            reads++;
            Assert.True(elapsed.ElapsedMilliseconds >= 490);
            Assert.Equal(2, driver.Actions.Count(action => action.StartsWith("MOVE(")));
            Assert.Equal(reads - 1, driver.Actions.Count(action => action == "REL(0,0,5)"));
            Assert.False(ctx.MagnetCommandStarted);
        };

        var pick = handler.PickAsync(ctx, source, CancellationToken.None);
        Assert.Equal(2, driver.Actions.Count);
        Assert.Equal(0, reads);
        if (result == "plate")
        {
            await pick;
            Assert.True(ctx.MagnetCommandStarted);
            Assert.Contains("MAGNET_ON", driver.Actions);
        }
        else
        {
            var error = result == "empty"
                ? await Assert.ThrowsAsync<InvalidOperationException>(() => pick)
                : (Exception)await Assert.ThrowsAsync<TimeoutException>(() => pick);
            Assert.Contains(result == "empty" ? "X11=0" : "X11 读取超时", error.Message);
            Assert.False(ctx.MagnetCommandStarted);
            Assert.DoesNotContain("MAGNET_ON", driver.Actions);
            Assert.Empty(deviceDriver.Writes);
        }
        Assert.Equal(result == "empty" ? 4 : 1, reads);
    }
}
