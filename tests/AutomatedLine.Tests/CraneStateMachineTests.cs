using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Alarms;
using AutomatedLine.Application.Configuration;
using AutomatedLine.Application.Line;
using AutomatedLine.Application.Reservations;
using AutomatedLine.Application.RightOfWay;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Application.Transfer;
using AutomatedLine.Application.Transfer.Handlers;
using AutomatedLine.Application.Workers;
using AutomatedLine.Domain.Cranes;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Domain.Line;
using AutomatedLine.Domain.Routing;
using AutomatedLine.Domain.Spatial;
using AutomatedLine.Domain.Tasks;
using AutomatedLine.Domain.Workpieces;
using AutomatedLine.Tests.Fakes;

namespace AutomatedLine.Tests;

/// <summary>
/// 编排器 RunFlowAsync 集成测试（打桩驱动）：
/// 入口暂停回滚、正常搬运、资源竞争回滚、充磁前保留和充磁后中止。
/// </summary>
public class CraneStateMachineTests
{
    /// <summary>测试装配：真实注册表/管理器 + 打桩驱动与配置。</summary>
    private sealed class Harness
    {
        public DeviceRegistry Devices { get; } = new();
        public DriverRegistry Drivers { get; } = new();
        public RuntimeTaskManager Tasks { get; } = new(new TestLogger());
        public CraneRegistry Cranes { get; } = new();
        public RightOfWayManager Row { get; }
        public ResourceReservationManager Reservations { get; }
        public LineControlService Line { get; }
        public AlarmManager Alarms { get; }
        public FakeConfigurationStore Store { get; } = new();
        public FakeConfigService Config { get; } = new();
        public CraneStateMachine Machine { get; }
        public Crane Crane { get; } = new(1, 1, "天车1")
        {
            SafeX = 0,
            SafeZ = 500,
            Width = 100,
            StartX = 0,
            EndX = 5000,
            IsEnabled = true
        };
        public FakeCraneDriver CraneDriver { get; } = new();
        public DeviceBase Source { get; }
        public DeviceBase Target { get; }
        public FakeDeviceDriver SourceDriver { get; }
        public FakeDeviceDriver TargetDriver { get; }

        /// <summary>装配真实状态机和桩驱动；默认启动线路，非运行入口测试可显式保留Stopped。</summary>
        /// <param name="source">本次搬运源设备。</param>
        /// <param name="target">本次搬运目标设备。</param>
        /// <param name="handshakeTimeoutMs">设备握手等待期限，单位毫秒。</param>
        /// <param name="startLine">是否按正常搬运前提启动线路。</param>
        public Harness(DeviceBase source, DeviceBase target, int handshakeTimeoutMs, bool startLine = true)
        {
            Source = source;
            Target = target;
            var logger = new TestLogger();
            Config.Options.StateMachine.HandshakeTimeoutMs = handshakeTimeoutMs;

            Row = new RightOfWayManager(logger);
            Reservations = new ResourceReservationManager(logger);
            Line = new LineControlService(logger);
            Alarms = new AlarmManager(new ActiveAlarmStore(), logger);
            var abort = new TaskAbortCoordinator(Tasks, Devices, Row, Reservations, Line, Alarms, logger);
            var handlers = new TransferHandlerRegistry(new ITransferHandler[]
            {
                new LoadRackHandler(), new BoringHandler(), new LatheHandler(),
                new GrinderHandler(), new MarkerHandler(), new UnloadRackHandler()
            });

            Machine = new CraneStateMachine(Devices, Drivers, Tasks, Row, Reservations, abort, handlers, Store, Config, Line, logger);

            Cranes.Register(Crane);
            Drivers.RegisterCrane(CraneDriver);
            Devices.Register(source);
            Devices.Register(target);
            SourceDriver = new FakeDeviceDriver(source.Id);
            TargetDriver = new FakeDeviceDriver(target.Id);
            Drivers.RegisterDevice(SourceDriver);
            Drivers.RegisterDevice(TargetDriver);
            Store.Devices.Add(new DeviceConfigDto { Id = source.Id, PositionX = 1000, PositionY = 0, PositionZ = 500 });
            Store.Devices.Add(new DeviceConfigDto { Id = target.Id, PositionX = 2000, PositionY = 0, PositionZ = 500 });
            if (startLine) Line.Start();
        }

        /// <summary>建一个料架→目标 的搬运任务。</summary>
        public (WorkpieceTask Wp, TransportTask Task) NewTask()
        {
            var wp = Tasks.Add(new WorkpieceInfo { Diameter = 100, Length = 700 });
            var task = new TransportTask
            {
                WorkpieceRuntimeId = wp.Id,
                SourceDeviceId = Source.Id,
                TargetDeviceId = Target.Id,
                SourceX = Source.PositionX,
                TargetX = Target.PositionX,
                TargetProcess = ProcessType.Boring
            };
            return (wp, task);
        }
    }

    /// <summary>出队后发现线路未运行时仅回滚分配，设备引用、快照及工件原位置全部保留。</summary>
    /// <param name="lineState">进入状态机时的非运行线路状态。</param>
    /// <param name="onDevice">工件是否已在源设备上，否则仍在上料队列。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(LineState.Stopped, false)]
    [InlineData(LineState.Stopped, true)]
    [InlineData(LineState.Paused, false)]
    [InlineData(LineState.Paused, true)]
    [InlineData(LineState.PausedByFault, false)]
    [InlineData(LineState.PausedByFault, true)]
    public async Task NotRunningAtEntry_RollsBackWithoutChangingDevices(LineState lineState, bool onDevice)
    {
        DeviceBase source = onDevice ? new BoringMachine(1, "源双头镗", 1000) : new LoadRack(1, "上料架", 1000);
        var h = new Harness(source, new BoringMachine(2, "目标双头镗", 2000), 10000,
            startLine: lineState != LineState.Stopped);
        h.Source.Signals = TestSignals.Of(("HasMaterial", true), ("RequestUnload", true));
        h.Target.Signals = TestSignals.Of(("RequestData", true), ("RequestLoad", true));
        var sourceSignals = h.Source.Signals;
        var targetSignals = h.Target.Signals;
        var (wp, task) = h.NewTask();
        wp.Runtime.TaskState = WorkpieceState.Assigned;
        wp.Runtime.CurrentLocation = onDevice ? WorkpieceLocation.AtDevice(source.Id) : WorkpieceLocation.Queue;
        wp.Runtime.CurrentProcess = onDevice ? ProcessType.Boring : ProcessType.Unknown;
        wp.Runtime.QueueOrder = 7;
        h.Source.CurrentWorkpiece = onDevice ? wp : null;
        var targetWorkpiece = h.Tasks.Add(new WorkpieceInfo { PlateNo = "其他工件" });
        h.Target.CurrentWorkpiece = targetWorkpiece;
        h.Crane.Runtime.State = CraneState.Reserved;
        h.Crane.Runtime.CurrentTask = task;
        var otherTaskId = Guid.NewGuid();
        Assert.True(h.Row.TryAcquire(otherTaskId, new XInterval(10000, 11000)));
        Assert.True(h.Reservations.TryReserve(otherTaskId, 20, 21));
        if (lineState == LineState.Paused) h.Line.Pause();
        if (lineState == LineState.PausedByFault) h.Line.EnterFaultPause();

        await h.Machine.RunFlowAsync(h.Crane, task, CancellationToken.None);

        Assert.Equal(lineState, h.Line.State);
        Assert.Equal(WorkpieceState.Waiting, wp.Runtime.TaskState);
        Assert.Equal(onDevice ? WorkpieceLocation.AtDevice(source.Id) : WorkpieceLocation.Queue, wp.Runtime.CurrentLocation);
        Assert.Equal(onDevice ? ProcessType.Boring : ProcessType.Unknown, wp.Runtime.CurrentProcess);
        Assert.Equal(7, wp.Runtime.QueueOrder);
        Assert.Same(onDevice ? wp : null, h.Source.CurrentWorkpiece);
        Assert.Same(targetWorkpiece, h.Target.CurrentWorkpiece);
        Assert.Equal(WorkpieceState.Created, targetWorkpiece.Runtime.TaskState);
        Assert.Same(sourceSignals, h.Source.Signals);
        Assert.Same(targetSignals, h.Target.Signals);
        Assert.True(h.Crane.IsIdle);
        Assert.Null(h.Crane.CurrentWorkpiece);
        Assert.Null(h.Crane.Runtime.LockedInterval);
        Assert.False(h.Row.IsHeld(task.TaskId));
        Assert.True(h.Row.IsHeld(otherTaskId));
        Assert.Single(h.Row.Snapshot());
        Assert.False(h.Reservations.IsReserved(source.Id));
        Assert.False(h.Reservations.IsReserved(h.Target.Id));
        Assert.True(h.Reservations.IsReserved(20));
        Assert.True(h.Reservations.IsReserved(21));
        Assert.Empty(h.CraneDriver.Actions);
        Assert.Empty(h.SourceDriver.Writes);
        Assert.Empty(h.TargetDriver.Writes);
        Assert.Empty(h.Alarms.Store.Snapshot());
    }

    /// <summary>暂停回滚后同一块设备工件仍能在恢复线路、重新派发后完成搬运。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task PausedAtEntry_SameWorkpieceCanRunAfterResume()
    {
        var h = new Harness(new BoringMachine(1, "源双头镗", 1000), new BoringMachine(2, "目标双头镗", 2000), 10000);
        h.Source.Signals = TestSignals.Of(("RequestUnload", true));
        h.Target.Signals = TestSignals.Of(("RequestData", true), ("RequestLoad", true));
        var (wp, task) = h.NewTask();
        wp.Runtime.TaskState = WorkpieceState.Assigned;
        wp.Runtime.CurrentLocation = WorkpieceLocation.AtDevice(h.Source.Id);
        wp.Runtime.CurrentProcess = ProcessType.Boring;
        wp.Runtime.QueueOrder = 7;
        h.Source.CurrentWorkpiece = wp;
        h.Line.Pause();

        await h.Machine.RunFlowAsync(h.Crane, task, CancellationToken.None);

        Assert.Same(wp, h.Source.CurrentWorkpiece);
        Assert.Equal(WorkpieceState.Waiting, wp.Runtime.TaskState);
        Assert.Empty(h.CraneDriver.Actions);
        h.Line.Start();
        Assert.True(wp.Runtime.TryTransition(WorkpieceState.Waiting, WorkpieceState.Assigned));
        await h.Machine.RunFlowAsync(h.Crane, task, CancellationToken.None);

        Assert.Same(wp, h.Target.CurrentWorkpiece);
        Assert.Null(h.Source.CurrentWorkpiece);
        Assert.Equal(WorkpieceLocation.AtDevice(h.Target.Id), wp.Runtime.CurrentLocation);
        Assert.Equal(WorkpieceState.Waiting, wp.Runtime.TaskState);
        Assert.Equal(7, wp.Runtime.QueueOrder);
        Assert.True(h.Crane.IsIdle);
        Assert.Null(h.Crane.CurrentWorkpiece);
        Assert.False(h.Row.IsHeld(task.TaskId));
        Assert.False(h.Reservations.IsReserved(h.Source.Id));
        Assert.False(h.Reservations.IsReserved(h.Target.Id));
        Assert.Empty(h.Alarms.Store.Snapshot());
    }

    /// <summary>入口未运行时仍优先处理已经接受的人工中止和宿主取消，不能静默保留后返回。</summary>
    /// <param name="manualAbort">是否人工中止，否则模拟关联的宿主令牌取消。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NotRunningAtEntry_CancellationKeepsOriginalCleanup(bool manualAbort)
    {
        var h = new Harness(new BoringMachine(1, "源双头镗", 1000), new BoringMachine(2, "目标双头镗", 2000), 10000);
        var (wp, task) = h.NewTask();
        wp.Runtime.TaskState = WorkpieceState.Assigned;
        wp.Runtime.CurrentLocation = WorkpieceLocation.AtDevice(h.Source.Id);
        h.Source.CurrentWorkpiece = wp;
        using var host = new CancellationTokenSource();
        await using var execution = new CraneTaskExecution(task, host.Token);
        if (manualAbort)
        {
            // 使用真实执行范围登记中止，不新增公开入口仅为测试绕过 Worker。
            var request = typeof(CraneTaskExecution).GetMethod("TryRequestManualAbort",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Assert.Equal(true, request.Invoke(execution, new object[] { (Action)h.Line.EnterFaultPause }));
        }
        else
        {
            h.Line.Pause();
            host.Cancel();
        }

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            h.Machine.RunFlowAsync(h.Crane, task, execution.Token, execution));

        Assert.Equal(manualAbort ? WorkpieceState.Discarded : WorkpieceState.Waiting, wp.Runtime.TaskState);
        Assert.Equal(manualAbort ? WorkpieceLocation.Discarded : WorkpieceLocation.AtDevice(h.Source.Id), wp.Runtime.CurrentLocation);
        Assert.Same(manualAbort ? null : wp, h.Source.CurrentWorkpiece);
        Assert.True(h.Crane.IsIdle);
        Assert.Null(h.Crane.CurrentWorkpiece);
        Assert.False(h.Row.IsHeld(task.TaskId));
        Assert.False(h.Reservations.IsReserved(h.Source.Id));
        Assert.Equal(LineState.PausedByFault, h.Line.State);
        Assert.Single(h.Alarms.Store.Snapshot());
        Assert.Empty(h.CraneDriver.Actions);
    }

    /// <summary>入口已通过的搬运在后续正常暂停时继续执行，不改变原有在途任务语义。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task PauseAfterEntry_DoesNotCancelStartedFlow()
    {
        var h = new Harness(new LoadRack(1, "上料架", 1000), new BoringMachine(2, "双头镗", 2000), 10000);
        h.Source.Signals = TestSignals.Of(("HasMaterial", true));
        h.Target.Signals = TestSignals.Of(("RequestData", true), ("RequestLoad", true));
        h.CraneDriver.OnReadHasPlate = h.Line.Pause;
        var (wp, task) = h.NewTask();

        await h.Machine.RunFlowAsync(h.Crane, task, CancellationToken.None);

        Assert.Equal(LineState.Paused, h.Line.State);
        Assert.Same(wp, h.Target.CurrentWorkpiece);
        Assert.Equal(WorkpieceLocation.AtDevice(h.Target.Id), wp.Runtime.CurrentLocation);
        Assert.Equal(WorkpieceState.Waiting, wp.Runtime.TaskState);
        Assert.True(h.Crane.IsIdle);
        Assert.False(h.Row.IsHeld(task.TaskId));
        Assert.Empty(h.Alarms.Store.Snapshot());
    }

    /// <summary>正常一条：料架取料 → 双头镗放料 → 释放全部、工件 Waiting、位置在目标。</summary>
    [Fact]
    public async Task NormalFlow_Completes_And_Releases()
    {
        var h = new Harness(new LoadRack(1, "上料架", 1000), new BoringMachine(2, "双头镗", 2000), handshakeTimeoutMs: 10000);
        h.Source.Signals = TestSignals.Of(("HasMaterial", true));
        h.Target.Signals = TestSignals.Of(("RequestData", true), ("RequestLoad", true));
        var (wp, task) = h.NewTask();

        await h.Machine.RunFlowAsync(h.Crane, task, CancellationToken.None);

        Assert.Equal(WorkpieceState.Waiting, wp.Runtime.TaskState);   // Boring 非下料 → 等下一段
        Assert.True(wp.Runtime.CurrentLocation.IsOnDevice);
        Assert.Equal(h.Target.Id, wp.Runtime.CurrentLocation.ResourceId);
        Assert.Null(h.Crane.Runtime.CurrentTask);
        Assert.Equal(CraneState.Idle, h.Crane.Runtime.State);
        Assert.False(h.Row.IsHeld(task.TaskId));
        Assert.False(h.Reservations.IsReserved(h.Source.Id));
        Assert.False(h.Reservations.IsReserved(h.Target.Id));
        Assert.Contains("SET_SPEED", h.CraneDriver.Actions);   // 流程开始先设速度
    }

    /// <summary>
    /// 中转要复位**源与目标两侧**的握手位，且顺序正确：
    /// 源侧 = 取料时写 1、中转再清 0（下料完成已发出，PLC 已采到）；
    /// 目标侧 = 中转先清 0、放料再写 1（保证设备收到干净的 0→1 上升沿，否则会白等到握手超时）。
    /// 用斜床作源、双头镗作目标，两侧都是"有上位机写入位"的设备，一次覆盖两条约定。
    /// </summary>
    [Fact]
    public async Task Transit_ClearsBothSides_SourceAfterItsWrite_TargetBeforePlaceWrite()
    {
        var h = new Harness(new LatheMachine(3, "斜床1", 1000), new BoringMachine(2, "双头镗", 2000), handshakeTimeoutMs: 10000);
        h.Source.Signals = TestSignals.Of(("RequestUnload", true), ("TailstockOpened", true));
        h.Target.Signals = TestSignals.Of(("RequestData", true), ("RequestLoad", true));
        var (_, task) = h.NewTask();

        await h.Machine.RunFlowAsync(h.Crane, task, CancellationToken.None);

        // 源侧（斜床）：取料写 1 → 中转清 0
        var sourceWrites = h.SourceDriver.Writes;
        var sourceSetAt = sourceWrites.IndexOf("CraneUnloadDone=1");
        var sourceClearAt = sourceWrites.IndexOf("CraneUnloadDone=0");
        Assert.True(sourceSetAt >= 0, "取料应写 天车下料完成=1");
        Assert.True(sourceClearAt >= 0, "中转应复位源设备握手位（写 0）");
        Assert.True(sourceSetAt < sourceClearAt, "源侧应先写 1、中转再清 0");

        // 目标侧（双头镗）：中转清 0 → 放料写 1；且"上位机写入位"全部清零（含目标阶段用不到的残留位）
        var targetWrites = h.TargetDriver.Writes;
        var targetClearAt = targetWrites.IndexOf("DataSentDone=0");
        var targetSetAt = targetWrites.IndexOf("DataSentDone=1");
        Assert.True(targetClearAt >= 0, "中转应复位目标设备握手位（写 0）");
        Assert.True(targetSetAt >= 0, "放料应写 数据下发完成=1");
        Assert.True(targetClearAt < targetSetAt, "目标侧必须先清 0、放料再写 1");
        Assert.Contains("LoadDone=0", targetWrites);
        Assert.Contains("UnloadDone=0", targetWrites);
    }

    /// <summary>源握手清理尚未完成时保持预约；清理结束或取消后均能释放全部资源。</summary>
    /// <param name="cancelDuringClear">是否在源握手清理期间取消本次搬运</param>
    /// <returns>异步验证任务</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transit_HoldsSourceReservationUntilHandshakeClearEnds(bool cancelDuringClear)
    {
        var h = new Harness(new BoringMachine(1, "源双头镗", 1000), new BoringMachine(2, "目标双头镗", 2000), handshakeTimeoutMs: 10000);
        h.Source.Signals = TestSignals.Of(("RequestUnload", true));
        h.Target.Signals = TestSignals.Of(("RequestData", true), ("RequestLoad", true));
        var (wp, task) = h.NewTask();
        h.Source.CurrentWorkpiece = wp;
        wp.Runtime.CurrentLocation = WorkpieceLocation.AtDevice(h.Source.Id);
        var blockingDriver = new BlockingHandshakeDriver(h.SourceDriver);
        h.Drivers.RegisterDevice(blockingDriver);
        h.CraneDriver.OnAbsoluteMove = (x, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (x == 2000)
            {
                // 中转后的目标放料运动到来时，源清理必须已结束且已让出设备。
                if (h.SourceDriver.Writes.Contains("UnloadDone=0"))
                {
                    Assert.False(h.Reservations.IsReserved(h.Source.Id));
                    Assert.True(h.Reservations.IsReserved(h.Target.Id));
                    Assert.Equal(new XInterval(1950, 2050), h.Crane.Runtime.LockedInterval);
                }
            }

            return Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.InPosition, "ok"));
        };
        using var cancellation = new CancellationTokenSource();
        var flow = h.Machine.RunFlowAsync(h.Crane, task, cancellation.Token);
        var competitor = Guid.NewGuid();
        try
        {
            await blockingDriver.ClearingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(h.Reservations.IsReserved(h.Source.Id));
            Assert.True(h.Reservations.IsReserved(h.Target.Id));
            Assert.False(h.Reservations.TryReserve(competitor, 99, h.Source.Id), "旧任务清理未结束时，新任务不能取得源设备");

            if (cancelDuringClear)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flow.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(WorkpieceState.Discarded, wp.Runtime.TaskState);
                Assert.Equal(LineState.PausedByFault, h.Line.State);
            }
            else
            {
                blockingDriver.AllowClear.TrySetResult();
                await flow.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Contains("DataSentDone=0", h.SourceDriver.Writes);
                Assert.Contains("LoadDone=0", h.SourceDriver.Writes);
                Assert.Contains("UnloadDone=0", h.SourceDriver.Writes);
                Assert.Same(wp, h.Target.CurrentWorkpiece);
                Assert.Equal(WorkpieceState.Waiting, wp.Runtime.TaskState);
            }

            Assert.False(h.Reservations.IsReserved(h.Source.Id));
            Assert.False(h.Reservations.IsReserved(h.Target.Id));
            Assert.False(h.Row.IsHeld(task.TaskId));
            Assert.True(h.Reservations.TryReserve(competitor, 99, h.Source.Id));
        }
        finally
        {
            blockingDriver.AllowClear.TrySetResult();
            // 即使断言失败也结束本次流程，不遗留仍在等待驱动的测试任务。
            try { await flow.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            h.Reservations.Release(competitor);
        }
    }

    /// <summary>仅延迟首次握手清零，后续写入仍由原有记录驱动处理。</summary>
    private sealed class BlockingHandshakeDriver : IDeviceDriver
    {
        private readonly FakeDeviceDriver _inner;

        /// <summary>通知测试：状态机已经进入源握手清理。</summary>
        public TaskCompletionSource ClearingStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>允许清零完成，模拟异步通信结束。</summary>
        public TaskCompletionSource AllowClear { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>包装已有驱动，保留取料及握手写入记录。</summary>
        /// <param name="inner">记录读写的设备驱动</param>
        public BlockingHandshakeDriver(FakeDeviceDriver inner) => _inner = inner;

        /// <inheritdoc />
        public int DeviceId => _inner.DeviceId;

        /// <inheritdoc />
        public bool IsConnected => _inner.IsConnected;

        /// <inheritdoc />
        public Task ConnectAsync(CancellationToken cancellationToken = default) => _inner.ConnectAsync(cancellationToken);

        /// <inheritdoc />
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => _inner.DisconnectAsync(cancellationToken);

        /// <inheritdoc />
        public Task<IReadOnlyDictionary<string, object?>> ReadAllSignalsAsync(CancellationToken cancellationToken = default)
            => _inner.ReadAllSignalsAsync(cancellationToken);

        /// <inheritdoc />
        public async Task WriteSignalAsync(string pointName, object? value, CancellationToken cancellationToken = default)
        {
            if (pointName == "DataSentDone" && value is int number && number == 0)
            {
                ClearingStarted.TrySetResult();
                await AllowClear.Task.WaitAsync(cancellationToken);
            }

            await _inner.WriteSignalAsync(pointName, value, cancellationToken);
        }
    }

    /// <summary>acquire 失败：路权被占 → 回滚、任务仍 Waiting、未动设备。</summary>
    [Fact]
    public async Task AcquireFails_RollsBack_ToWaiting()
    {
        var h = new Harness(new LoadRack(1, "上料架", 1000), new BoringMachine(2, "双头镗", 2000), handshakeTimeoutMs: 10000);
        var (wp, task) = h.NewTask();
        // 预先占住与任务区间 [950,2050] 相交的一段路权
        Assert.True(h.Row.TryAcquire(Guid.NewGuid(), new XInterval(1000, 1500)));

        await h.Machine.RunFlowAsync(h.Crane, task, CancellationToken.None);

        Assert.Equal(WorkpieceState.Waiting, wp.Runtime.TaskState);
        Assert.NotNull(h.Tasks.Get(wp.Id));
        Assert.Null(h.Crane.Runtime.CurrentTask);
        Assert.Equal(CraneState.Idle, h.Crane.Runtime.State);
    }

    /// <summary>充磁前源未就绪超时，保留队列工件与排位，释放资源并暂停线路。</summary>
    [Fact]
    public async Task SourceNotReady_PreservesWorkpiece_Releases_And_Pauses()
    {
        var h = new Harness(new LoadRack(1, "上料架", 1000), new BoringMachine(2, "双头镗", 2000), handshakeTimeoutMs: 0);
        // 上料架无板 → 取料等待超时
        var (wp, task) = h.NewTask();

        await h.Machine.RunFlowAsync(h.Crane, task, CancellationToken.None);

        var retained = h.Tasks.Get(wp.Id);
        Assert.Same(wp, retained);
        Assert.Equal(WorkpieceState.Waiting, retained!.Runtime.TaskState);
        Assert.Equal(WorkpieceLocation.Queue, retained.Runtime.CurrentLocation);
        Assert.Equal(LineState.PausedByFault, h.Line.State);            // 已暂停
        Assert.Null(h.Crane.Runtime.CurrentTask);
        Assert.False(h.Row.IsHeld(task.TaskId));                        // 路权已释放
        Assert.False(h.Reservations.IsReserved(h.Source.Id));           // 源预约已释放
        Assert.False(h.Reservations.IsReserved(h.Target.Id));           // 目标预约已释放
    }

    /// <summary>充磁前失败保留设备上的工件身份、位置、工序与排位，恢复后同一工件可完成搬运。</summary>
    /// <returns>异步验证任务</returns>
    [Fact]
    public async Task BeforeMagnet_PreservesSourceWorkpiece_AndCanRetry()
    {
        var h = new Harness(new BoringMachine(1, "源双头镗", 1000), new BoringMachine(2, "目标双头镗", 2000), handshakeTimeoutMs: 0);
        h.Target.Signals = TestSignals.Of(("RequestData", true), ("RequestLoad", true));
        var (wp, task) = h.NewTask();
        h.Source.CurrentWorkpiece = wp;
        wp.Runtime.CurrentLocation = WorkpieceLocation.AtDevice(h.Source.Id);
        wp.Runtime.CurrentProcess = ProcessType.Boring;
        wp.Runtime.QueueOrder = 7;

        await h.Machine.RunFlowAsync(h.Crane, task, CancellationToken.None);

        Assert.Same(wp, h.Source.CurrentWorkpiece);
        Assert.Equal(WorkpieceState.Waiting, wp.Runtime.TaskState);
        Assert.Equal(WorkpieceLocation.AtDevice(h.Source.Id), wp.Runtime.CurrentLocation);
        Assert.Equal(ProcessType.Boring, wp.Runtime.CurrentProcess);
        Assert.Equal(7, wp.Runtime.QueueOrder);
        Assert.Null(h.Crane.CurrentWorkpiece);
        Assert.Equal(CraneState.Idle, h.Crane.Runtime.State);
        Assert.False(h.Row.IsHeld(task.TaskId));
        Assert.False(h.Reservations.IsReserved(h.Source.Id));
        Assert.False(h.Reservations.IsReserved(h.Target.Id));
        Assert.Equal(LineState.PausedByFault, h.Line.State);

        h.Source.Signals = TestSignals.Of(("RequestUnload", true));
        h.Line.Start();
        await h.Machine.RunFlowAsync(h.Crane, task, CancellationToken.None);

        Assert.Same(wp, h.Target.CurrentWorkpiece);
        Assert.Null(h.Source.CurrentWorkpiece);
        Assert.Equal(WorkpieceLocation.AtDevice(h.Target.Id), wp.Runtime.CurrentLocation);
        Assert.Equal(WorkpieceState.Waiting, wp.Runtime.TaskState);
    }

    /// <summary>X11 无板或读取失败时保留源设备上的同一工件，恢复线路后仍可执行。</summary>
    /// <param name="communicationFailure">是否模拟 X11 通信失败，否则模拟无板</param>
    /// <returns>异步验证任务</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlateCheckFailure_PreservesSourceAndCanRetry(bool communicationFailure)
    {
        var h = new Harness(new BoringMachine(1, "源双头镗", 1000), new BoringMachine(2, "目标双头镗", 2000), handshakeTimeoutMs: 0);
        h.Source.Signals = TestSignals.Of(("RequestUnload", true));
        h.Target.Signals = TestSignals.Of(("RequestData", true), ("RequestLoad", true));
        h.CraneDriver.Status = new() { HasPlate = false };
        h.CraneDriver.HasPlateException = communicationFailure ? new TimeoutException("X11 读取超时") : null;
        var (wp, task) = h.NewTask();
        h.Source.CurrentWorkpiece = wp;
        wp.Runtime.CurrentLocation = WorkpieceLocation.AtDevice(h.Source.Id);
        wp.Runtime.CurrentProcess = ProcessType.Boring;
        wp.Runtime.QueueOrder = 7;

        await h.Machine.RunFlowAsync(h.Crane, task, CancellationToken.None);

        Assert.Same(wp, h.Source.CurrentWorkpiece);
        Assert.Same(wp, h.Tasks.Get(wp.Id));
        Assert.Equal(WorkpieceState.Waiting, wp.Runtime.TaskState);
        Assert.Equal(WorkpieceLocation.AtDevice(h.Source.Id), wp.Runtime.CurrentLocation);
        Assert.Equal(ProcessType.Boring, wp.Runtime.CurrentProcess);
        Assert.Equal(7, wp.Runtime.QueueOrder);
        Assert.DoesNotContain("MAGNET_ON", h.CraneDriver.Actions);
        Assert.Empty(h.SourceDriver.Writes);
        Assert.Null(h.Crane.Runtime.CurrentTask);
        Assert.Equal(CraneState.Idle, h.Crane.Runtime.State);
        Assert.False(h.Row.IsHeld(task.TaskId));
        Assert.False(h.Reservations.IsReserved(h.Source.Id));
        Assert.False(h.Reservations.IsReserved(h.Target.Id));
        Assert.Equal(LineState.PausedByFault, h.Line.State);

        h.CraneDriver.Status = new() { HasPlate = true };
        h.CraneDriver.HasPlateException = null;
        h.Line.Start();
        await h.Machine.RunFlowAsync(h.Crane, task, CancellationToken.None);
        Assert.Null(h.Source.CurrentWorkpiece);
        Assert.Same(wp, h.Target.CurrentWorkpiece);
        Assert.Equal(WorkpieceLocation.AtDevice(h.Target.Id), wp.Runtime.CurrentLocation);
    }

    /// <summary>取料已开始调用充磁时，命令失败、触发写入或反馈失败均必须弃板。</summary>
    /// <param name="failure">命令失败、反馈超时、触发写入异常或内部取消</param>
    /// <returns>异步验证任务</returns>
    [Theory]
    [InlineData("command")]
    [InlineData("feedback")]
    [InlineData("write")]
    [InlineData("internal-cancel")]
    public async Task PickMagnetFailure_DiscardsAfterCallingMagnet(string failure)
    {
        var h = new Harness(new BoringMachine(1, "源双头镗", 1000), new BoringMachine(2, "目标双头镗", 2000), handshakeTimeoutMs: 0);
        h.Source.Signals = TestSignals.Of(("RequestUnload", true));
        var (wp, task) = h.NewTask();
        h.Source.CurrentWorkpiece = wp;
        wp.Runtime.CurrentLocation = WorkpieceLocation.AtDevice(h.Source.Id);
        switch (failure)
        {
            case "command":
                h.CraneDriver.MagnetResult = CraneCommandResult.Fail(CraneCommandCode.Rejected, "X6=0，充磁反馈未确认");
                break;
            case "feedback":
                h.CraneDriver.MagnetResult = CraneCommandResult.Fail(CraneCommandCode.FeedbackTimeout, "充磁反馈超时");
                break;
            case "write":
                h.CraneDriver.MagnetException = new TimeoutException("充磁写入响应超时");
                break;
            default:
                h.CraneDriver.MagnetException = new OperationCanceledException("充磁内部取消");
                break;
        }

        await h.Machine.RunFlowAsync(h.Crane, task, CancellationToken.None);

        Assert.Equal(WorkpieceState.Discarded, wp.Runtime.TaskState);
        Assert.Equal(WorkpieceLocation.Discarded, wp.Runtime.CurrentLocation);
        Assert.Null(h.Source.CurrentWorkpiece);
        Assert.Null(h.Crane.Runtime.CurrentTask);
        Assert.Equal(CraneState.Idle, h.Crane.Runtime.State);
        Assert.False(h.Row.IsHeld(task.TaskId));
        Assert.False(h.Reservations.IsReserved(h.Source.Id));
        Assert.False(h.Reservations.IsReserved(h.Target.Id));
        Assert.Equal(LineState.PausedByFault, h.Line.State);
        Assert.Empty(h.SourceDriver.Writes);
        Assert.Empty(h.TargetDriver.Writes);
    }

    /// <summary>去源设备运动失败保留工件；放料退磁后回安全位失败仍弃板并清目标引用。</summary>
    /// <param name="afterPlace">是否在放料完成后的收尾运动注入异常</param>
    /// <returns>异步验证任务</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MovementFailure_PreservesBeforeMagnet_DiscardsEvenAfterDemagnet(bool afterPlace)
    {
        var h = new Harness(new BoringMachine(1, "源双头镗", 1000), new BoringMachine(2, "目标双头镗", 2000), handshakeTimeoutMs: 0);
        h.Source.Signals = TestSignals.Of(("RequestUnload", true));
        h.Target.Signals = TestSignals.Of(("RequestData", true), ("RequestLoad", true));
        h.CraneDriver.FailAbsoluteMoveX = afterPlace ? 0 : 1000;
        var (wp, task) = h.NewTask();
        h.Source.CurrentWorkpiece = wp;
        wp.Runtime.CurrentLocation = WorkpieceLocation.AtDevice(h.Source.Id);

        await h.Machine.RunFlowAsync(h.Crane, task, CancellationToken.None);

        Assert.Equal(afterPlace ? WorkpieceState.Discarded : WorkpieceState.Waiting, wp.Runtime.TaskState);
        if (afterPlace)
        {
            Assert.Contains("MAGNET_OFF", h.CraneDriver.Actions);
            Assert.Null(h.Source.CurrentWorkpiece);
            Assert.Equal(WorkpieceLocation.Discarded, wp.Runtime.CurrentLocation);
        }
        else
        {
            Assert.DoesNotContain("MAGNET_ON", h.CraneDriver.Actions);
            Assert.Same(wp, h.Source.CurrentWorkpiece);
            Assert.Equal(WorkpieceLocation.AtDevice(h.Source.Id), wp.Runtime.CurrentLocation);
        }
        Assert.Null(h.Target.CurrentWorkpiece);
        Assert.Null(h.Crane.Runtime.CurrentTask);
        Assert.False(h.Row.IsHeld(task.TaskId));
        Assert.False(h.Reservations.IsReserved(h.Source.Id));
        Assert.False(h.Reservations.IsReserved(h.Target.Id));
        Assert.Equal(LineState.PausedByFault, h.Line.State);
    }

    /// <summary>充磁后异常（目标未就绪）：Abort 弃板 + 暂停线路 + 释放全部。</summary>
    [Fact]
    public async Task AfterMagnet_Failure_Aborts_And_Pauses()
    {
        var h = new Harness(new LoadRack(1, "上料架", 1000), new BoringMachine(2, "双头镗", 2000), handshakeTimeoutMs: 0);
        h.Source.Signals = TestSignals.Of(("HasMaterial", true));   // 取料成功（充磁 → HasPieceOnCrane=true）
        // 目标请求数据始终为 false → 放料等待超时 → Abort
        var (wp, task) = h.NewTask();

        await h.Machine.RunFlowAsync(h.Crane, task, CancellationToken.None);

        var discarded = h.Tasks.Get(wp.Id);
        Assert.NotNull(discarded);                                      // 保留记录（标记丢弃，不删除）
        Assert.Equal(WorkpieceState.Discarded, discarded!.Runtime.TaskState);
        Assert.Equal(LineState.PausedByFault, h.Line.State);            // 已暂停
        Assert.Null(h.Crane.Runtime.CurrentTask);
        Assert.False(h.Reservations.IsReserved(h.Source.Id));
        Assert.False(h.Reservations.IsReserved(h.Target.Id));
    }
}
