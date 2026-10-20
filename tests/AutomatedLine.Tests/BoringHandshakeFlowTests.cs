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

/// <summary>验证带托料架双头镗取放顺序，回升失败或取消时禁止通知完成。</summary>
public class BoringHandshakeFlowTests
{
    private sealed class Harness
    {
        /// <summary>仅提供新协议的三个请求输入，不模拟旧卡钳反馈。</summary>
        public BoringMachine Device { get; } = new(1, "双头镗", 1000);
        /// <summary>记录设备写入。</summary>
        public FakeDeviceDriver Driver { get; } = new(1);
        /// <summary>记录运动及充退磁。</summary>
        public FakeCraneDriver Crane { get; } = new();
        /// <summary>真实搬运上下文。</summary>
        public TransferContext Context { get; }

        /// <summary>装配真实上下文；零等待期限确保不会暗中依赖旧卡钳反馈。</summary>
        public Harness()
        {
            var registry = new DriverRegistry();
            registry.RegisterDevice(Driver);
            registry.RegisterCrane(Crane);
            var store = new FakeConfigurationStore();
            store.Devices.Add(new DeviceConfigDto { Id = 1, PositionX = 1000, PositionY = 0, PositionZ = 500 });
            var options = new AppOptions();
            options.StateMachine.HandshakeTimeoutMs = 0;
            var wp = new WorkpieceTask(new WorkpieceInfo { Length = 1200.4, Diameter = 180.6, PlugHole = 70, PlugThickness = 12.7 });
            Context = new TransferContext(new Crane(1, 1, "天车1") { SafeZ = 500 }, wp, registry, store, options, new TestLogger());
            SetRequests(true);
        }

        /// <summary>同时设置三个设备请求。</summary>
        /// <param name="ready">请求是否满足。</param>
        public void SetRequests(bool ready) => Device.Signals = TestSignals.Of(
            ("RequestData", ready), ("RequestLoad", ready), ("RequestUnload", ready));

        /// <summary>执行真实取放料。</summary>
        /// <param name="pick">是否取料。</param>
        /// <param name="token">当前任务令牌。</param>
        /// <returns>搬运步骤任务。</returns>
        public Task Run(bool pick, CancellationToken token) => pick
            ? new BoringHandler().PickAsync(Context, Device, token)
            : new BoringHandler().PlaceAsync(Context, Device, token);
    }

    /// <summary>三个请求即可完成，完成通知必须晚于充退磁和安全回升。</summary>
    /// <param name="pick">是否取料，否则放料。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transfer_CompletesWithoutIntermediateHandshake(bool pick)
    {
        var h = new Harness();
        h.Driver.OnWrite = (point, _, _) =>
        {
            if (point == (pick ? "UnloadDone" : "LoadDone"))
            {
                Assert.Contains(pick ? "MAGNET_ON" : "MAGNET_OFF", h.Crane.Actions);
                Assert.Equal(pick ? 3 : 2, h.Crane.Actions.Count(a => a.StartsWith("MOVE(")));
                Assert.EndsWith(",500)", h.Crane.Actions.Last());
            }
            return Task.CompletedTask;
        };
        await h.Run(pick, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(pick ? "UnloadDone=1" : "LoadDone=1", h.Driver.Writes.Last());
        Assert.Equal(pick, h.Context.MagnetCommandStarted);
        Assert.Equal(pick ? new[] { "UnloadDone" } : new[] { "Length", "Diameter", "BorePlug", "PlugThickness", "DataSentDone", "LoadDone" },
            h.Driver.Writes.Select(w => w.Split('=')[0]));
        Assert.Equal(1200.4, h.Context.Workpiece.Info.Length);
        Assert.Equal(180.6, h.Context.Workpiece.Info.Diameter);
        Assert.Equal(12.7, h.Context.Workpiece.Info.PlugThickness);
        if (!pick) Assert.Contains("BorePlug=70", h.Driver.Writes);
    }

    /// <summary>305应答未完成时禁止写剩余参数、数据完成或开始运动，避免PLC使用不完整数据。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task BorePlug_PendingAcknowledgement_BlocksDataDone()
    {
        var h = new Harness();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Driver.OnWrite = (point, _, token) =>
        {
            if (point != "BorePlug") return Task.CompletedTask;
            started.SetResult();
            return acknowledged.Task.WaitAsync(token);
        };
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var placing = h.Run(false, guard.Token);
        await started.Task.WaitAsync(guard.Token);
        try
        {
            Assert.False(placing.IsCompleted);
            Assert.DoesNotContain("PlugThickness=12.7", h.Driver.Writes);
            Assert.DoesNotContain("DataSentDone=1", h.Driver.Writes);
            Assert.Empty(h.Crane.Actions);
        }
        finally
        {
            acknowledged.TrySetResult();
            await placing.WaitAsync(guard.Token);
        }
        Assert.Contains("DataSentDone=1", h.Driver.Writes);
    }

    /// <summary>设备未请求时禁止动作，仍按原期限报错。</summary>
    /// <param name="pick">是否取料。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestTimeout_StopsFollowingActions(bool pick)
    {
        var h = new Harness();
        h.SetRequests(false);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Run(pick, CancellationToken.None));
        Assert.Contains(pick ? "RequestUnload" : "RequestData", error.Message);
        Assert.Empty(h.Crane.Actions);
        Assert.Empty(h.Driver.Writes);
    }

    /// <summary>回升取消或失败时禁止完成通知，避免设备提前开始下一阶段。</summary>
    /// <param name="pick">是否取料。</param>
    /// <param name="cancel">是否模拟取消，否则运动超时。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RiseFailureOrCancellation_DoesNotPublishDone(bool pick, bool cancel)
    {
        var h = new Harness();
        using var cts = new CancellationTokenSource();
        var moves = 0;
        h.Crane.OnAbsoluteMove = (_, token) =>
        {
            if (++moves == (pick ? 3 : 2))
            {
                if (cancel)
                {
                    cts.Cancel();
                    token.ThrowIfCancellationRequested();
                }
                return Task.FromResult(CraneCommandResult.Fail(CraneCommandCode.Timeout, "模拟回升超时"));
            }
            return Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.InPosition, "ok"));
        };
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Run(pick, cts.Token));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => h.Run(pick, cts.Token));
        Assert.Contains(pick ? "MAGNET_ON" : "MAGNET_OFF", h.Crane.Actions);
        Assert.DoesNotContain(pick ? "UnloadDone=1" : "LoadDone=1", h.Driver.Writes);
    }

    /// <summary>参数失败不能发送数据完成或开始运动。</summary>
    /// <param name="point">模拟写入失败的加工参数。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData("Length")]
    [InlineData("Diameter")]
    [InlineData("BorePlug")]
    [InlineData("PlugThickness")]
    public async Task ParameterFailure_DoesNotPublishDataDone(string point)
    {
        var h = new Harness();
        h.Driver.OnWrite = (name, _, _) => name == point
            ? Task.FromException(new IOException("模拟参数写入失败")) : Task.CompletedTask;
        await Assert.ThrowsAsync<IOException>(() => h.Run(false, CancellationToken.None));
        Assert.DoesNotContain("DataSentDone=1", h.Driver.Writes);
        Assert.Empty(h.Crane.Actions);
    }
}
