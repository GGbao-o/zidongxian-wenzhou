using AutomatedLine.Application.Configuration;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Application.Transfer;
using AutomatedLine.Domain.Cranes;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Domain.Signals;
using AutomatedLine.Domain.Workpieces;
using AutomatedLine.Tests.Fakes;

namespace AutomatedLine.Tests;

/// <summary>
/// TransferContext 原语测试：设备信号等待**读缓存快照**（采集器写入），
/// 且 expected=false 时不能把"没数据"当成"无料"。
/// </summary>
public class TransferContextTests
{
    /// <summary>构造一条缓存信号快照。</summary>
    private static SignalSnapshot Snap(params (string Key, object? Value)[] items)
        => new(DateTime.Now, 1, ConnectionState.Connected, SignalQuality.Good,
            items.ToDictionary(i => i.Key, i => i.Value));

    /// <summary>构造上下文（设备信号由测试直接写入 device.Signals 缓存）。</summary>
    private static TransferContext Build(DeviceBase device, int handshakeTimeoutMs)
    {
        var crane = new Crane(1, 1, "天车1") { SafeZ = 500 };
        var drivers = new DriverRegistry();
        drivers.RegisterCrane(new FakeCraneDriver());
        drivers.RegisterDevice(new FakeDeviceDriver(device.Id));

        var options = new AppOptions();
        options.StateMachine.HandshakeTimeoutMs = handshakeTimeoutMs;

        return new TransferContext(crane, new WorkpieceTask(new WorkpieceInfo()), drivers, new FakeConfigurationStore(), options, new TestLogger());
    }

    /// <summary>缓存有信号=false、期望 false → 立即满足。</summary>
    [Fact]
    public async Task WaitDeviceAsync_False_Present_Returns()
    {
        var device = new UnloadRack(9, "下料架", 9000) { Signals = Snap(("HasMaterial", false)) };
        var ctx = Build(device, handshakeTimeoutMs: 5000);

        await ctx.WaitDeviceAsync(device, "HasMaterial", expected: false, CancellationToken.None);
    }

    /// <summary>缓存无该信号、期望 false → **不满足**（没采到不能当"无料"），超时抛异常。</summary>
    [Fact]
    public async Task WaitDeviceAsync_False_Missing_DoesNotSatisfy()
    {
        var device = new UnloadRack(9, "下料架", 9000) { Signals = Snap() };
        var ctx = Build(device, handshakeTimeoutMs: 0);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ctx.WaitDeviceAsync(device, "HasMaterial", expected: false, CancellationToken.None));
    }

    /// <summary>无快照（未采集）、期望 false → 也不满足。</summary>
    [Fact]
    public async Task WaitDeviceAsync_False_NullSnapshot_DoesNotSatisfy()
    {
        var device = new UnloadRack(9, "下料架", 9000);
        var ctx = Build(device, handshakeTimeoutMs: 0);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ctx.WaitDeviceAsync(device, "HasMaterial", expected: false, CancellationToken.None));
    }

    /// <summary>缓存有信号=true、期望 true → 满足。</summary>
    [Fact]
    public async Task WaitDeviceAsync_True_Present_Returns()
    {
        var device = new LoadRack(1, "上料架", 1000) { Signals = Snap(("HasMaterial", true)) };
        var ctx = Build(device, handshakeTimeoutMs: 5000);

        await ctx.WaitDeviceAsync(device, "HasMaterial", expected: true, CancellationToken.None);
    }
}
