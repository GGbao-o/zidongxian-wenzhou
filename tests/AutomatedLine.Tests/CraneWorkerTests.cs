using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Alarms;
using AutomatedLine.Application.Configuration;
using AutomatedLine.Application.Generation;
using AutomatedLine.Application.Line;
using AutomatedLine.Application.Reservations;
using AutomatedLine.Application.RightOfWay;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Application.Scheduling;
using AutomatedLine.Application.Transfer;
using AutomatedLine.Application.Workers;
using AutomatedLine.Domain.Cranes;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Domain.Line;
using AutomatedLine.Domain.Routing;
using AutomatedLine.Domain.Tasks;
using AutomatedLine.Domain.Workpieces;
using AutomatedLine.Tests.Fakes;

namespace AutomatedLine.Tests;

/// <summary>
/// 验证非宿主取消只中止当前搬运，真实 Worker 在人工恢复线路后仍可消费下一单。
/// </summary>
public class CraneWorkerTests
{
    private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(5);

    /// <summary>流程内及初始化发生内部取消异常时，按充磁边界处理工件，Worker 恢复后仍可完成下一单。</summary>
    /// <param name="failBeforeFlow">是否在状态机初始化时注入异常</param>
    /// <param name="afterMagnet">是否在充磁指令开始下发之后注入异常</param>
    /// <returns>异步验证任务</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task UnexpectedCancellation_EndsCurrentTransport_AndWorkerExecutesNext(bool failBeforeFlow, bool afterMagnet)
    {
        var h = new Harness();
        await h.Worker.StartAsync(CancellationToken.None);
        try
        {
            await h.Config.Ready.Task.WaitAsync(GuardTimeout);
            if (failBeforeFlow)
            {
                // 初始化尚未创建上下文也必须保留工件，不能落入弃板兜底。
                h.Config.FailNextRead();
            }
            else
            {
                h.Handler.MagnetizeBeforePick = afterMagnet;
                h.Handler.OnPick = _ => Task.FromException(new OperationCanceledException("驱动内部超时"));
            }

            var (first, firstTransport) = h.NewTask();
            Assert.True(h.Channels.For(h.Crane.Id).Writer.TryWrite(firstTransport));
            h.Line.Start();
            await h.FaultRaised.Task.WaitAsync(GuardTimeout);

            var expected = afterMagnet ? WorkpieceState.Discarded : WorkpieceState.Waiting;
            Assert.Equal(expected, first.Runtime.TaskState);
            Assert.Equal(LineState.PausedByFault, h.Line.State);
            Assert.Null(h.Crane.Runtime.CurrentTask);
            Assert.False(h.Row.IsHeld(firstTransport.TaskId));
            Assert.False(h.Reservations.IsReserved(h.Source.Id));
            Assert.False(h.Reservations.IsReserved(h.Target.Id));

            h.Handler.OnPick = _ => Task.CompletedTask;
            var (second, secondTransport) = h.NewTask();
            Assert.True(h.Channels.For(h.Crane.Id).Writer.TryWrite(secondTransport));
            h.Line.Start();
            await WaitForStateAsync(second, WorkpieceState.Completed);

            Assert.Equal(expected, first.Runtime.TaskState);
            Assert.Equal(h.Target.Id, second.Runtime.CurrentLocation.ResourceId);
            Assert.Null(h.Crane.Runtime.CurrentTask);
            Assert.Equal(CraneState.Idle, h.Crane.Runtime.State);
            Assert.False(h.Row.IsHeld(secondTransport.TaskId));
            Assert.False(h.Reservations.IsReserved(h.Target.Id));
        }
        finally
        {
            using var stopDeadline = new CancellationTokenSource(GuardTimeout);
            await h.Worker.StopAsync(stopDeadline.Token);
        }
    }

    /// <summary>未取消的调用者遇到充磁前内部取消异常时，状态机保留工件并记录真实原因。</summary>
    /// <returns>异步验证任务</returns>
    [Fact]
    public async Task StateMachine_InternalCancellation_IsHandledAsFailure()
    {
        var h = new Harness();
        h.Handler.OnPick = _ => Task.FromException(new OperationCanceledException("驱动内部超时"));
        var (workpiece, transport) = h.NewTask();

        await h.Machine.RunFlowAsync(h.Crane, transport, CancellationToken.None);

        Assert.Equal(WorkpieceState.Waiting, workpiece.Runtime.TaskState);
        Assert.Equal(LineState.PausedByFault, h.Line.State);
        Assert.Contains(h.Logger.Messages, message => message.Contains("步骤[取料(上料架)] 异常：驱动内部超时"));
        Assert.False(h.Row.IsHeld(transport.TaskId));
    }

    /// <summary>执行器令牌确实取消时，状态机清理资源后仍向调用者传播取消。</summary>
    /// <param name="afterMagnet">是否已开始下发充磁指令</param>
    /// <returns>异步验证任务</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StateMachine_CallerCancellation_PropagatesAfterCleanup(bool afterMagnet)
    {
        var h = new Harness();
        using var cancellation = new CancellationTokenSource();
        h.Handler.MagnetizeBeforePick = afterMagnet;
        h.Handler.OnPick = token =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(token);
        };
        var (workpiece, transport) = h.NewTask();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => h.Machine.RunFlowAsync(h.Crane, transport, cancellation.Token));

        Assert.Equal(afterMagnet ? WorkpieceState.Discarded : WorkpieceState.Waiting, workpiece.Runtime.TaskState);
        Assert.Null(h.Crane.Runtime.CurrentTask);
        Assert.False(h.Row.IsHeld(transport.TaskId));
        Assert.False(h.Reservations.IsReserved(h.Source.Id));
        Assert.False(h.Reservations.IsReserved(h.Target.Id));
    }

    /// <summary>停止 Worker 会取消正在等待的搬运并释放资源，不把真实停机当作可恢复通信故障。</summary>
    /// <param name="afterMagnet">是否已开始下发充磁指令</param>
    /// <returns>异步验证任务</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopAsync_CancelsInFlightTask_AndReleasesResources(bool afterMagnet)
    {
        var h = new Harness();
        h.Handler.MagnetizeBeforePick = afterMagnet;
        var enteredPick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Handler.OnPick = token =>
        {
            enteredPick.TrySetResult();
            return Task.Delay(Timeout.Infinite, token);
        };
        var (workpiece, transport) = h.NewTask();
        Assert.True(h.Channels.For(h.Crane.Id).Writer.TryWrite(transport));
        h.Line.Start();
        await h.Worker.StartAsync(CancellationToken.None);
        try
        {
            await enteredPick.Task.WaitAsync(GuardTimeout);
            using var stopDeadline = new CancellationTokenSource(GuardTimeout);
            await h.Worker.StopAsync(stopDeadline.Token);

            Assert.Equal(afterMagnet ? WorkpieceState.Discarded : WorkpieceState.Waiting, workpiece.Runtime.TaskState);
            Assert.Equal(LineState.PausedByFault, h.Line.State);
            Assert.Null(h.Crane.Runtime.CurrentTask);
            Assert.False(h.Row.IsHeld(transport.TaskId));
            Assert.False(h.Reservations.IsReserved(h.Source.Id));
            Assert.False(h.Reservations.IsReserved(h.Target.Id));
        }
        finally
        {
            using var stopDeadline = new CancellationTokenSource(GuardTimeout);
            await h.Worker.StopAsync(stopDeadline.Token);
        }
    }

    /// <summary>充磁前失败后，真实生成器和调度器重新派发同一工件，后台消费者仍能完成。</summary>
    /// <returns>异步验证任务</returns>
    [Fact]
    public async Task BeforeMagnetFailure_WorkerExecutesSameWorkpieceAfterResume()
    {
        var h = new Harness();
        h.Source.Signals = TestSignals.Of(("HasMaterial", true));
        h.Target.Signals = TestSignals.Of(("HasMaterial", false));
        h.Target.MaxDiameter = 1000;
        h.Target.MaxLength = 5000;
        var pool = new CandidateTaskPool();
        var generator = new TransportTaskGenerator(h.Tasks, h.Devices, pool, h.Reservations, h.Line, h.Config, h.Logger);
        var scheduler = new CraneScheduler(pool, h.Channels, h.Cranes, h.Drivers, h.Row, h.Reservations, h.Line, h.Config, h.Logger);
        h.Handler.OnPick = _ => Task.FromException(new TimeoutException("去源设备运动超时"));
        var (workpiece, first) = h.NewTask();
        workpiece.Runtime.QueueOrder = 9;
        Assert.True(h.Channels.For(h.Crane.Id).Writer.TryWrite(first));
        h.Line.Start();
        await h.Worker.StartAsync(CancellationToken.None);
        try
        {
            await h.FaultRaised.Task.WaitAsync(GuardTimeout);
            Assert.Equal(WorkpieceState.Waiting, workpiece.Runtime.TaskState);
            Assert.Equal(WorkpieceLocation.Queue, workpiece.Runtime.CurrentLocation);
            Assert.Equal(9, workpiece.Runtime.QueueOrder);
            Assert.Same(workpiece, h.Tasks.Get(workpiece.Id));

            h.Handler.OnPick = _ => Task.CompletedTask;
            await generator.StartAsync(CancellationToken.None);
            await scheduler.StartAsync(CancellationToken.None);
            h.Line.Start();
            await WaitForStateAsync(workpiece, WorkpieceState.Completed);
            Assert.Equal(h.Target.Id, workpiece.Runtime.CurrentLocation.ResourceId);
            Assert.Null(h.Crane.Runtime.CurrentTask);
            Assert.False(h.Row.IsHeld(first.TaskId));
            Assert.False(h.Reservations.IsReserved(h.Target.Id));
        }
        finally
        {
            using var stopDeadline = new CancellationTokenSource(GuardTimeout);
            await scheduler.StopAsync(stopDeadline.Token);
            await generator.StopAsync(stopDeadline.Token);
            await h.Worker.StopAsync(stopDeadline.Token);
        }
    }

    // 等待真实后台循环产出的状态，期限仅用于防止旧代码永久退出后测试挂住。
    private static async Task WaitForStateAsync(WorkpieceTask task, WorkpieceState expected)
    {
        using var deadline = new CancellationTokenSource(GuardTimeout);
        while (task.Runtime.TaskState != expected)
        {
            await Task.Delay(10, deadline.Token);
        }
    }

    /// <summary>装配真实执行链，脚本只替代设备动作和配置读取。</summary>
    private sealed class Harness
    {
        public TestLogger Logger { get; } = new();
        public CraneRegistry Cranes { get; } = new();
        public DeviceRegistry Devices { get; } = new();
        public DriverRegistry Drivers { get; } = new();
        public CraneTaskChannels Channels { get; } = new();
        public RuntimeTaskManager Tasks { get; }
        public RightOfWayManager Row { get; }
        public ResourceReservationManager Reservations { get; }
        public LineControlService Line { get; }
        public ScriptedConfigService Config { get; } = new();
        public ScriptedHandler Handler { get; } = new();
        public CraneStateMachine Machine { get; }
        public CraneWorker Worker { get; }
        public TaskCompletionSource FaultRaised { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Crane Crane { get; } = new(1, 1, "天车1")
        {
            IsEnabled = true, StartX = 0, EndX = 5000, Width = 100, SafeX = 0, SafeZ = 500
        };
        public LoadRack Source { get; } = new(1, "上料架", 1000);
        public UnloadRack Target { get; } = new(2, "下料架", 2000);

        /// <summary>使用同一套管理器装配状态机和 Worker，确保回归覆盖真正的 Channel 消费。</summary>
        public Harness()
        {
            Tasks = new RuntimeTaskManager(Logger);
            Row = new RightOfWayManager(Logger);
            Reservations = new ResourceReservationManager(Logger);
            Line = new LineControlService(Logger);
            var alarms = new AlarmManager(new ActiveAlarmStore(), Logger);
            alarms.Raised += (_, _) => FaultRaised.TrySetResult();
            var abort = new TaskAbortCoordinator(Tasks, Devices, Row, Reservations, Line, alarms, Logger);
            var store = new FakeConfigurationStore();
            store.Devices.Add(new DeviceConfigDto { Id = Source.Id, PositionX = 1000, PositionZ = 500 });
            store.Devices.Add(new DeviceConfigDto { Id = Target.Id, PositionX = 2000, PositionZ = 500 });
            Cranes.Register(Crane);
            Devices.Register(Source);
            Devices.Register(Target);
            Drivers.RegisterCrane(new FakeCraneDriver());
            Machine = new CraneStateMachine(Devices, Drivers, Tasks, Row, Reservations, abort,
                new TransferHandlerRegistry(new[] { Handler }), store, Config, Logger);
            Worker = new CraneWorker(Cranes, Channels, Machine, Line, Config, abort, Logger);
        }

        /// <summary>建立已派发任务，模拟调度器成功入队前的状态。</summary>
        /// <returns>工件记录和对应搬运任务</returns>
        public (WorkpieceTask Workpiece, TransportTask Transport) NewTask()
        {
            var workpiece = Tasks.Add(new WorkpieceInfo { Diameter = 100, Length = 700 });
            workpiece.Runtime.TaskState = WorkpieceState.Assigned;
            return (workpiece, new TransportTask
            {
                WorkpieceRuntimeId = workpiece.Id,
                SourceDeviceId = Source.Id,
                TargetDeviceId = Target.Id,
                SourceX = Source.PositionX,
                TargetX = Target.PositionX,
                TargetProcess = ProcessType.Unloading
            });
        }
    }

    /// <summary>可在流程入口注入一次非调用者取消，隔离验证 Worker 的兜底条件。</summary>
    private sealed class ScriptedConfigService : IConfigService
    {
        private int _failNextRead;
        private readonly AppOptions _options = new()
        {
            StateMachine = new StateMachineOptions { ExecuteIntervalMs = 50 }
        };

        /// <summary>首次读取完成的同步信号，用来确保 Worker 已完成启动参数读取。</summary>
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>让下一次配置读取抛出内部取消异常。</summary>
        public void FailNextRead() => Interlocked.Exchange(ref _failNextRead, 1);

        /// <inheritdoc />
        public AppOptions GetAppOptions()
        {
            if (Interlocked.Exchange(ref _failNextRead, 0) == 1)
            {
                throw new OperationCanceledException("流程入口内部超时");
            }
            Ready.TrySetResult();
            return _options;
        }

        /// <inheritdoc />
        public string GetConnectionString() => string.Empty;

        /// <inheritdoc />
        public void SaveAppOptions(AppOptions options) => throw new NotSupportedException("测试不写配置");
    }

    /// <summary>脚本化取料，仅隔离外部设备动作，保留状态机和资源管理流程。</summary>
    private sealed class ScriptedHandler : ITransferHandler
    {
        /// <summary>取料行为，可返回成功、内部取消或等待真正的执行器取消。</summary>
        public Func<CancellationToken, Task> OnPick { get; set; } = _ => Task.CompletedTask;

        /// <summary>先通过真实上下文开始充磁，再执行脚本，以覆盖两侧异常边界。</summary>
        public bool MagnetizeBeforePick { get; set; }

        /// <inheritdoc />
        public bool CanHandle(DeviceType type) => type is DeviceType.LoadRack or DeviceType.UnloadRack;

        /// <inheritdoc />
        public async Task PickAsync(TransferContext ctx, DeviceBase source, CancellationToken cancellationToken)
        {
            if (MagnetizeBeforePick)
            {
                ctx.MagnetCommandStarted = true;
                await ctx.MagnetOnAsync(cancellationToken);
            }
            await OnPick(cancellationToken);
        }

        /// <inheritdoc />
        public Task PlaceAsync(TransferContext ctx, DeviceBase target, CancellationToken cancellationToken)
            => Task.CompletedTask;

        /// <inheritdoc />
        public Task<int> ClearHandshakeAsync(IDeviceDriver driver, string deviceName, IAppLogger logger,
            CancellationToken cancellationToken) => Task.FromResult(0);
    }
}
