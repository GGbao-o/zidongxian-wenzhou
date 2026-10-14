using AutomatedLine.Application.Configuration;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Application.Transfer;
using AutomatedLine.Application.Transfer.Handlers;
using AutomatedLine.Domain.Cranes;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Domain.Workpieces;
using AutomatedLine.Tests.Fakes;

namespace AutomatedLine.Tests;

/// <summary>验证双头镗到位/卡钳反馈/退出完成的真实Handler顺序及失败、取消边界。</summary>
public class BoringHandshakeFlowTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);

    private sealed class Harness
    {
        /// <summary>被测试的双头镗，缓存由测试控制卡钳反馈。</summary>
        public BoringMachine Device { get; } = new(1, "双头镗", 1000);
        /// <summary>记录参数及握手写入的设备桩。</summary>
        public FakeDeviceDriver Driver { get; } = new(1);
        /// <summary>记录充退磁和运动顺序的天车桩。</summary>
        public FakeCraneDriver Crane { get; } = new();
        /// <summary>使用真实上下文等待和异常处理原语。</summary>
        public TransferContext Context { get; }

        /// <summary>装配真实Handler所需上下文与可控缓存。</summary>
        /// <param name="timeoutMs">设备握手等待期限，毫秒。</param>
        public Harness(int timeoutMs = 10000)
        {
            var registry = new DriverRegistry();
            registry.RegisterDevice(Driver);
            registry.RegisterCrane(Crane);
            var store = new FakeConfigurationStore();
            store.Devices.Add(new DeviceConfigDto { Id = 1, PositionX = 1000, PositionY = 0, PositionZ = 500 });
            var options = new AppOptions();
            options.StateMachine.HandshakeTimeoutMs = timeoutMs;
            var wp = new WorkpieceTask(new WorkpieceInfo { Length = 1200.4, Diameter = 180.6, PlugThickness = 12.7 });
            Context = new TransferContext(new Crane(1, 1, "天车1") { SafeZ = 500 }, wp, registry, store, options, new TestLogger());
            SetFeedback(false);
        }

        /// <summary>只改变卡钳完成反馈，保持三个请求已满足。</summary>
        /// <param name="ready">夹紧及松开反馈是否成立。</param>
        public void SetFeedback(bool ready) => Device.Signals = TestSignals.Of(
            ("RequestData", true), ("RequestLoad", true), ("RequestUnload", true),
            ("LoadLockDone", ready), ("UnloadReleaseDone", ready));

        /// <summary>执行取料或放料完整Handler。</summary>
        /// <param name="pick">是否取料。</param>
        /// <param name="token">当前搬运取消令牌。</param>
        /// <returns>异步取放任务。</returns>
        public Task Run(bool pick, CancellationToken token) => pick
            ? new BoringHandler().PickAsync(Context, Device, token)
            : new BoringHandler().PlaceAsync(Context, Device, token);
    }

    /// <summary>确认收到反馈前既不退磁/回升也不发退出完成，反馈通过后才完成动作。</summary>
    /// <param name="pick">是否验证取料，否则验证放料。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Feedback_GatesReleaseAndRise(bool pick)
    {
        var h = new Harness();
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Driver.OnWrite = (point, _, _) =>
        {
            if (point == (pick ? "UnloadInPlace" : "LoadInPlace")) notified.TrySetResult();
            return Task.CompletedTask;
        };
        var flow = h.Run(pick, CancellationToken.None);
        await notified.Task.WaitAsync(Guard);
        Assert.False(flow.IsCompleted);
        Assert.DoesNotContain("MAGNET_OFF", h.Crane.Actions);
        Assert.Equal(pick ? 2 : 1, h.Crane.Actions.Count(a => a.StartsWith("MOVE(")));
        Assert.DoesNotContain(pick ? "UnloadDone=1" : "LoadDone=1", h.Driver.Writes);

        h.SetFeedback(true);
        await flow.WaitAsync(Guard);
        Assert.Equal(pick ? "UnloadDone=1" : "LoadDone=1", h.Driver.Writes.Last());
        Assert.Equal(pick, h.Context.MagnetCommandStarted);
        Assert.EndsWith(",500)", h.Crane.Actions.Last());
        if (!pick)
        {
            Assert.Equal(new[] { "Length", "Diameter", "PlugThickness", "DataSentDone", "LoadInPlace", "LoadDone" },
                h.Driver.Writes.Select(w => w.Split('=')[0]));
            Assert.Equal(1200.4, h.Context.Workpiece.Info.Length);
            Assert.Equal(180.6, h.Context.Workpiece.Info.Diameter);
            Assert.Equal(12.7, h.Context.Workpiece.Info.PlugThickness);
            Assert.Equal("MAGNET_OFF", h.Crane.Actions[^2]);
        }
    }

    /// <summary>缺少卡钳完成反馈时超时，不执行后续退磁/回升和完成通知。</summary>
    /// <param name="pick">是否验证取料，否则验证放料。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FeedbackTimeout_StopsFollowingActions(bool pick)
    {
        var h = new Harness(0);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Run(pick, CancellationToken.None));
        Assert.Contains(pick ? "UnloadReleaseDone" : "LoadLockDone", error.Message);
        Assert.DoesNotContain("MAGNET_OFF", h.Crane.Actions);
        Assert.Equal(pick ? 2 : 1, h.Crane.Actions.Count(a => a.StartsWith("MOVE(")));
        Assert.DoesNotContain(pick ? "UnloadDone=1" : "LoadDone=1", h.Driver.Writes);
    }

    /// <summary>在到位通知后取消当前流程，反馈等待传播取消且不继续动作。</summary>
    /// <param name="pick">是否验证取料，否则验证放料。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAtFeedback_StopsFollowingActions(bool pick)
    {
        var h = new Harness();
        using var cts = new CancellationTokenSource();
        h.Driver.OnWrite = (point, _, _) =>
        {
            if (point == (pick ? "UnloadInPlace" : "LoadInPlace")) cts.Cancel();
            return Task.CompletedTask;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Run(pick, cts.Token));
        Assert.DoesNotContain("MAGNET_OFF", h.Crane.Actions);
        Assert.Equal(pick ? 2 : 1, h.Crane.Actions.Count(a => a.StartsWith("MOVE(")));
        Assert.DoesNotContain(pick ? "UnloadDone=1" : "LoadDone=1", h.Driver.Writes);
    }

    /// <summary>参数写入失败不能发送数据完成或开始Z运动。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task ParameterFailure_DoesNotPublishDataDone()
    {
        var h = new Harness();
        h.SetFeedback(true);
        h.Driver.OnWrite = (point, _, _) => point == "Diameter"
            ? Task.FromException(new IOException("模拟参数写入失败")) : Task.CompletedTask;
        await Assert.ThrowsAsync<IOException>(() => h.Run(false, CancellationToken.None));
        Assert.DoesNotContain("DataSentDone=1", h.Driver.Writes);
        Assert.Empty(h.Crane.Actions);
    }
}
