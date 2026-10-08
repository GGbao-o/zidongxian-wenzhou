using System.Diagnostics;
using AutomatedLine.Application.Configuration;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Application.Transfer;
using AutomatedLine.Application.Transfer.Handlers;
using AutomatedLine.Domain.Cranes;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Domain.Workpieces;
using AutomatedLine.Tests.Fakes;

namespace AutomatedLine.Tests;

/// <summary>验证各源设备在取料下降后等待信号稳定，并以单次 X11 读取作为充磁前条件。</summary>
public class PickPlateCheckTests
{
    /// <summary>五种源设备都只读一次 X11；无板和通信失败不能开始充磁或进行后续握手。</summary>
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
    public async Task PickChecksOnceAfterSettlingBeforeMagnet(int sourceKind, string result)
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
        options.Transfer.Lathes[1] = new LatheTransferOptions { TailstockCenterDistance = 800 };
        var ctx = new TransferContext(crane, new WorkpieceTask(new WorkpieceInfo { Diameter = 100, Length = 700 }),
            drivers, store, options, new TestLogger());
        var reads = 0;
        var elapsed = Stopwatch.StartNew();
        driver.OnReadHasPlate = () =>
        {
            reads++;
            Assert.True(elapsed.ElapsedMilliseconds >= 490);
            Assert.Equal(2, driver.Actions.Count); // XY 到源上方、Z 到取料位，尚未充磁。
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
        Assert.Equal(1, reads);
    }
}
