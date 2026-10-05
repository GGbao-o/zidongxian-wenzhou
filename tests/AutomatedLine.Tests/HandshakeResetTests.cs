using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Transfer;
using AutomatedLine.Application.Transfer.Handlers;
using AutomatedLine.Tests.Fakes;

namespace AutomatedLine.Tests;

/// <summary>
/// "上位机写入握手位"复位测试（取料后自动复位 / 手动页复用同一实现）。
/// 覆盖三条约定：**哪些设备要清**、**清成什么值（0）**、**清不掉不影响流程（尽力清）**。
/// </summary>
public class HandshakeResetTests
{
    /// <summary>双头镗：复位 3 个握手位，全部写 0。</summary>
    [Fact]
    public async Task Boring_ClearsThreePointsToZero()
    {
        var driver = new FakeDeviceDriver(2);

        var cleared = await new BoringHandler()
            .ClearHandshakeAsync(driver, "双头镗", new TestLogger(), CancellationToken.None);

        Assert.Equal(3, cleared);
        Assert.Equal(new[] { "DataSentDone=0", "LoadDone=0", "UnloadDone=0" }, driver.Writes);
    }

    /// <summary>研磨机：复位 5 个握手位，全部写 0。</summary>
    [Fact]
    public async Task Grinder_ClearsFivePointsToZero()
    {
        var driver = new FakeDeviceDriver(6);

        var cleared = await new GrinderHandler()
            .ClearHandshakeAsync(driver, "研磨机1", new TestLogger(), CancellationToken.None);

        Assert.Equal(5, cleared);
        Assert.Equal(
            new[] { "DataTransDone=0", "LoadInPlace=0", "LoadDone=0", "UnloadInPlace=0", "UnloadDone=0" },
            driver.Writes);
    }

    /// <summary>斜床：复位 5 个握手宏，全部写 0。</summary>
    [Fact]
    public async Task Lathe_ClearsFivePointsToZero()
    {
        var driver = new FakeDeviceDriver(3);

        var cleared = await new LatheHandler()
            .ClearHandshakeAsync(driver, "斜床1", new TestLogger(), CancellationToken.None);

        Assert.Equal(5, cleared);
        Assert.Equal(
            new[]
            {
                "DataSentDone=0", "CraneLoadInPlace=0", "CraneLoadDone=0",
                "CraneUnloadInPlace=0", "CraneUnloadDone=0"
            },
            driver.Writes);
    }

    /// <summary>打号机 / 上料架 / 下料架：没有上位机写入位，一个点都不写。</summary>
    /// <param name="kind">设备类别</param>
    [Theory]
    [InlineData("marker")]
    [InlineData("loadrack")]
    [InlineData("unloadrack")]
    public async Task DevicesWithoutHandshakePoints_WriteNothing(string kind)
    {
        var driver = new FakeDeviceDriver(8);

        ITransferHandler handler = kind switch
        {
            "marker" => new MarkerHandler(),
            "loadrack" => new LoadRackHandler(),
            _ => new UnloadRackHandler()
        };

        var cleared = await handler.ClearHandshakeAsync(driver, "设备", new TestLogger(), CancellationToken.None);

        Assert.Equal(0, cleared);
        Assert.Empty(driver.Writes);
    }

    /// <summary>
    /// 尽力清：单点写失败**不抛异常**、流程不受影响（最坏退回"残留 1"的旧行为），
    /// 且三个点都会尝试过。
    /// </summary>
    [Fact]
    public async Task WriteFailure_IsSwallowed_AndAllPointsStillAttempted()
    {
        var driver = new FailingDeviceDriver();

        var cleared = await new BoringHandler()
            .ClearHandshakeAsync(driver, "双头镗", new TestLogger(), CancellationToken.None);

        Assert.Equal(0, cleared);
        Assert.Equal(3, driver.Attempts);
    }

    /// <summary>写必失败的驱动：验证"尽力清"的容错分支。</summary>
    private sealed class FailingDeviceDriver : IDeviceDriver
    {
        /// <summary>尝试写入的次数。</summary>
        public int Attempts { get; private set; }

        /// <inheritdoc />
        public int DeviceId => 2;

        /// <inheritdoc />
        public bool IsConnected => true;

        /// <inheritdoc />
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        /// <inheritdoc />
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        /// <inheritdoc />
        public Task<IReadOnlyDictionary<string, object?>> ReadAllSignalsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());

        /// <inheritdoc />
        public Task WriteSignalAsync(string pointName, object? value, CancellationToken ct = default)
        {
            Attempts++;
            throw new InvalidOperationException("模拟写失败");
        }
    }
}
