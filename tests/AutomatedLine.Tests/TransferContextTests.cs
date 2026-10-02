using AutomatedLine.Application.Configuration;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Application.Transfer;
using AutomatedLine.Domain.Cranes;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Domain.Workpieces;
using AutomatedLine.Tests.Fakes;

namespace AutomatedLine.Tests;

/// <summary>
/// TransferContext 原语测试：设备信号等待（尤其 expected=false 时不能把"没数据"当成"无料"）。
/// </summary>
public class TransferContextTests
{
    /// <summary>构造上下文与打桩设备驱动。</summary>
    private static TransferContext Build(DeviceBase device, FakeDeviceDriver driver, int handshakeTimeoutMs)
    {
        var crane = new Crane(1, 1, "天车1") { SafeZ = 500 };
        var drivers = new DriverRegistry();
        drivers.RegisterCrane(new FakeCraneDriver());
        drivers.RegisterDevice(driver);

        var options = new AppOptions();
        options.StateMachine.HandshakeTimeoutMs = handshakeTimeoutMs;

        return new TransferContext(crane, new WorkpieceTask(new WorkpieceInfo()), drivers, new FakeConfigurationStore(), options, new TestLogger());
    }

    /// <summary>信号存在且为 false、期望 false → 立即满足。</summary>
    [Fact]
    public async Task WaitDeviceAsync_False_Present_Returns()
    {
        var device = new UnloadRack(9, "下料架", 9000);
        var driver = new FakeDeviceDriver(9);
        driver.Signals["HasMaterial"] = false;
        var ctx = Build(device, driver, handshakeTimeoutMs: 5000);

        await ctx.WaitDeviceAsync(device, "HasMaterial", expected: false, CancellationToken.None);
    }

    /// <summary>信号缺失、期望 false → **不满足**（未采到不能当"无料"），超时抛异常。</summary>
    [Fact]
    public async Task WaitDeviceAsync_False_Missing_DoesNotSatisfy()
    {
        var device = new UnloadRack(9, "下料架", 9000);
        var driver = new FakeDeviceDriver(9);   // 没有任何信号
        var ctx = Build(device, driver, handshakeTimeoutMs: 0);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ctx.WaitDeviceAsync(device, "HasMaterial", expected: false, CancellationToken.None));
    }

    /// <summary>信号存在且为 true、期望 true → 满足。</summary>
    [Fact]
    public async Task WaitDeviceAsync_True_Present_Returns()
    {
        var device = new LoadRack(1, "上料架", 1000);
        var driver = new FakeDeviceDriver(1);
        driver.Signals["HasMaterial"] = true;
        var ctx = Build(device, driver, handshakeTimeoutMs: 5000);

        await ctx.WaitDeviceAsync(device, "HasMaterial", expected: true, CancellationToken.None);
    }
}
