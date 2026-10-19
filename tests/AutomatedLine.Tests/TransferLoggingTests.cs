using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Configuration;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Application.Transfer;
using AutomatedLine.Domain.Cranes;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Domain.Signals;
using AutomatedLine.Domain.Tasks;
using AutomatedLine.Domain.Workpieces;
using AutomatedLine.Tests.Fakes;

namespace AutomatedLine.Tests;

/// <summary>验证新增日志能区分等待、写入和失败，并且不增加天车读取或改变原动作。</summary>
public sealed class TransferLoggingTests
{
    /// <summary>缓存的明确无板与未知必须记录成不同结果，避免日志误导现场。</summary>
    /// <param name="known">是否已有明确无板的采集结果。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WaitDevice_LogsActualSnapshotAndOutcomeWithoutCraneReads(bool known)
    {
        var device = new UnloadRack(9, "下料架", 9000);
        if (known)
            device.Signals = new SignalSnapshot(DateTime.Now, 42, ConnectionState.Connected,
                SignalQuality.Good, new Dictionary<string, object?> { ["HasMaterial"] = false });
        var (ctx, _, driver, _, logger) = Build();

        if (known) await ctx.WaitDeviceAsync(device, "HasMaterial", false, CancellationToken.None);
        else await Assert.ThrowsAsync<InvalidOperationException>(
            () => ctx.WaitDeviceAsync(device, "HasMaterial", false, CancellationToken.None));

        Assert.Contains(logger.Messages, m => m.Contains("期望=0") && m.Contains("来源=后台采集缓存"));
        Assert.Contains(logger.Messages, m => m.Contains(known ? "原值=0(Boolean)" : "未知（尚无快照）"));
        Assert.Contains(logger.Messages, m => m.Contains(known ? "HasMaterial 满足" : "HasMaterial 超时"));
        if (known) Assert.Contains(logger.Messages, m => m.Contains("序号=42"));
        Assert.Equal(0, driver.StatusReadCount);
        Assert.Equal(0, driver.HasPlateReadCount);
        Assert.Empty(driver.Actions);
    }

    /// <summary>运动失败必须记录真实结果码，原运动只调用一次且不附加诊断读取。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task MoveFailure_LogsResultWithoutAdditionalMotionOrReads()
    {
        var (ctx, _, driver, _, logger) = Build();
        driver.FailAbsoluteMoveX = 101;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ctx.MoveAbsoluteAsync(100.6, 20, 30, CancellationToken.None));

        Assert.Equal(new[] { "MOVE(101,20,30)" }, driver.Actions);
        Assert.Equal(0, driver.StatusReadCount);
        Assert.Equal(0, driver.HasPlateReadCount);
        Assert.Contains(logger.Messages, m => m.Contains("取整目标XYZ=(101,20,30)"));
        Assert.Contains(logger.Messages, m => m.StartsWith("W:") && m.Contains("结果码=Timeout") && m.Contains("运动超时"));
    }

    /// <summary>写入开始先于真实调用，只有驱动返回成功才记录完成；失败仍向上传播。</summary>
    /// <param name="fail">是否模拟通信失败。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteDevice_CompletionRequiresSuccessfulDriverReturn(bool fail)
    {
        var (ctx, _, _, driver, logger) = Build();
        var device = new BoringMachine(driver.DeviceId, "双头镗", 1000);
        driver.OnWrite = (_, _, _) =>
        {
            Assert.Contains(logger.Messages, m => m.Contains("写入开始") && m.Contains("点名=LoadDone"));
            Assert.DoesNotContain(logger.Messages, m => m.Contains("写入完成"));
            return fail ? Task.FromException(new IOException("写入应答失败")) : Task.CompletedTask;
        };

        if (fail) await Assert.ThrowsAsync<IOException>(() => ctx.WriteDeviceAsync(device, "LoadDone", 1, CancellationToken.None));
        else await ctx.WriteDeviceAsync(device, "LoadDone", 1, CancellationToken.None);

        Assert.Equal(new[] { "LoadDone=1" }, driver.Writes);
        Assert.Equal(!fail, logger.Messages.Any(m => m.Contains("写入完成") && m.Contains("下发值=1(Int32)")));
    }

    /// <summary>收尾清任务后仍记录原搬运标识，且转义打印内容不会修改实际下发数据。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task Logging_KeepsTaskIdentityAndOriginalPrintPayload()
    {
        var (ctx, crane, _, driver, logger) = Build();
        var taskId = crane.Runtime.CurrentTask!.TaskId;
        crane.Runtime.CurrentTask = null;
        var device = new MarkerMachine(driver.DeviceId, "打号机", 1000);
        const string content = "板号A\r\n第二行";

        await ctx.WriteDeviceAsync(device, "PrintContent", content, CancellationToken.None);

        Assert.Equal(content, driver.Signals["PrintContent"]);
        Assert.All(logger.Messages, m => Assert.Contains($"搬运={taskId:N}", m));
        Assert.Contains(logger.Messages, m => m.Contains("板号A\\r\\n第二行"));
    }

    /// <summary>复位记录应明确每个点的成败，继续后续点的原容错顺序保持不变。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task HandshakeReset_LogsEachAttemptAndPreservesPartialFailurePolicy()
    {
        var (_, _, _, driver, logger) = Build();
        driver.OnWrite = (point, _, _) => point == "LoadDone"
            ? Task.FromException(new IOException("模拟通信失败")) : Task.CompletedTask;

        var cleared = await HandshakeReset.ClearAsync(driver, "双头镗",
            new[] { "DataSentDone", "LoadDone", "UnloadDone" }, logger, CancellationToken.None);

        Assert.Equal(2, cleared);
        Assert.Equal(new[] { "DataSentDone=0", "LoadDone=0", "UnloadDone=0" }, driver.Writes);
        Assert.Contains(logger.Messages, m => m.Contains("握手位 LoadDone 复位失败"));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("握手位写入完成：LoadDone=0"));
        Assert.Contains(logger.Messages, m => m.Contains("握手位写入完成：UnloadDone=0"));
    }

    // 共用真实上下文与原有驱动桩，以动作记录和下发内容验证日志没有引入额外业务动作。
    private static (TransferContext Context, Crane Crane, FakeCraneDriver CraneDriver, FakeDeviceDriver DeviceDriver, TestLogger Logger) Build()
    {
        var crane = new Crane(1, 1, "天车1");
        crane.Runtime.CurrentTask = new TransportTask();
        var craneDriver = new FakeCraneDriver();
        var deviceDriver = new FakeDeviceDriver(2);
        var drivers = new DriverRegistry();
        drivers.RegisterCrane(craneDriver);
        drivers.RegisterDevice(deviceDriver);
        var logger = new TestLogger();
        var options = new AppOptions();
        options.StateMachine.HandshakeTimeoutMs = 0;
        var context = new TransferContext(crane, new WorkpieceTask(new WorkpieceInfo()), drivers,
            new FakeConfigurationStore(), options, logger);
        return (context, crane, craneDriver, deviceDriver, logger);
    }
}
