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

    /// <summary>打号机只复位两个握手输出，保留打印内容、尺寸及PLC只读状态。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task Marker_ClearsHandshakeButPreservesParametersAndInputs()
    {
        var driver = new FakeDeviceDriver(8);
        driver.Signals["LoadDone"] = 1;
        driver.Signals["UnloadDone"] = 1;
        driver.Signals["PrintContent"] = "板号123";
        driver.Signals["RollerOuterDiameter"] = 100;
        driver.Signals["RollerInnerDiameter"] = 70;
        driver.Signals["ProcessDone"] = true;

        var cleared = await new MarkerHandler()
            .ClearHandshakeAsync(driver, "打号机", new TestLogger(), CancellationToken.None);

        Assert.Equal(2, cleared);
        Assert.Equal(new[] { "LoadDone=0", "UnloadDone=0" }, driver.Writes);
        Assert.Equal("板号123", driver.Signals["PrintContent"]);
        Assert.Equal(100, driver.Signals["RollerOuterDiameter"]);
        Assert.Equal(70, driver.Signals["RollerInnerDiameter"]);
        Assert.Equal(true, driver.Signals["ProcessDone"]);
    }

    /// <summary>打号机写入被取消时向上传播，不继续尝试第二个握手位。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task Marker_CancellationStopsRemainingWrites()
    {
        var driver = new FailingDeviceDriver();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MarkerHandler()
            .ClearHandshakeAsync(driver, "打号机", new TestLogger(), cts.Token));

        Assert.Equal(0, driver.Attempts);
    }

    /// <summary>上料架 / 下料架：没有使用中的握手输出，一个点都不写。</summary>
    /// <param name="kind">设备类别</param>
    [Theory]
    [InlineData("loadrack")]
    [InlineData("unloadrack")]
    public async Task DevicesWithoutHandshakePoints_WriteNothing(string kind)
    {
        var driver = new FakeDeviceDriver(8);

        ITransferHandler handler = kind switch
        {
            "loadrack" => new LoadRackHandler(),
            _ => new UnloadRackHandler()
        };

        var cleared = await handler.ClearHandshakeAsync(driver, "设备", new TestLogger(), CancellationToken.None);

        Assert.Equal(0, cleared);
        Assert.Empty(driver.Writes);
    }

    /// <summary>
    /// 尽力清：单点写失败**不抛异常**、流程不受影响（最坏退回"残留 1"的旧行为），
    /// 且后续点仍会尝试。
    /// </summary>
    /// <param name="marker">是否验证打号机的两点清理。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteFailure_IsSwallowed_AndAllPointsStillAttempted(bool marker)
    {
        var driver = new FailingDeviceDriver();
        ITransferHandler handler = marker ? new MarkerHandler() : new BoringHandler();

        var cleared = await handler.ClearHandshakeAsync(driver, "设备", new TestLogger(), CancellationToken.None);

        Assert.Equal(0, cleared);
        Assert.Equal(marker ? 2 : 3, driver.Attempts);
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
            ct.ThrowIfCancellationRequested();
            Attempts++;
            throw new InvalidOperationException("模拟写失败");
        }
    }
}
