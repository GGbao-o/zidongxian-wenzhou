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
    /// <summary>源微调超限保留工件，目标微调超限按充磁后弃板并尽力清源握手，两种情形均禁止相对运动。</summary>
    /// <param name="atTarget">是否在取料完成后的目标上方注入超限。</param>
    /// <returns>异步流程验证任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefinementOverLimit_UsesOriginalFailureAndCleanupPolicy(bool atTarget)
    {
        var h = new Harness(new BoringMachine(1, "源", 1000), new BoringMachine(2, "目标", 2000), 1000);
        h.Source.Signals = TestSignals.Of(("RequestUnload", true));
        var (wp, task) = h.NewTask();
        h.Source.CurrentWorkpiece = wp;
        wp.Runtime.CurrentLocation = WorkpieceLocation.AtDevice(h.Source.Id);
        h.CraneDriver.OnReadStatus = _ =>
        {
            var status = h.CraneDriver.Status;
            return Task.FromResult<CraneStatusDto?>(status?.XEncoder == (atTarget ? 2000 : 1000)
                ? new() { XEncoder = status.XEncoder + 201, YEncoder = 0 } : status);
        };
        await h.Machine.RunFlowAsync(h.Crane, task, default);
        Assert.DoesNotContain(h.CraneDriver.Actions, a => a.StartsWith("REL("));
        Assert.Equal(atTarget ? WorkpieceState.Discarded : WorkpieceState.Waiting, wp.Runtime.TaskState);
        Assert.Same(atTarget ? null : wp, h.Source.CurrentWorkpiece);
        Assert.Empty(h.TargetDriver.Writes);
        Assert.Contains(h.Logger.Messages, m => m.Contains("XY微调超限") && m.Contains("超限轴=X"));
        if (atTarget) Assert.Contains("UnloadDone=0", h.SourceDriver.Writes);
        else Assert.Empty(h.SourceDriver.Writes);
        Assert.Equal(LineState.PausedByFault, h.Line.State);
        Assert.False(h.Row.IsHeld(task.TaskId));
        Assert.False(h.Reservations.IsReserved(h.Source.Id));
        Assert.False(h.Reservations.IsReserved(h.Target.Id));
    }

    /// <summary>取料交接后源引用虽已空，中转失败仍应清本任务源握手，持有资源直到清理结束。</summary>
    /// <returns>异步流程验证任务。</returns>
    [Fact]
    public async Task FailureCleanup_EmptySourceStillClearsBeforeReleasingReservation()
    {
        var h = new Harness(new BoringMachine(1, "源", 1000), new BoringMachine(2, "目标", 2000), 1000);
        h.Source.Signals = TestSignals.Of(("RequestUnload", true));
        var (wp, task) = h.NewTask();
        h.Source.CurrentWorkpiece = wp;
        h.CraneDriver.FailAbsoluteMoveX = 2000;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.SourceDriver.OnWrite = async (point, value, token) =>
        {
            if (point != "DataSentDone" || !Equals(value, 0)) return;
            started.TrySetResult();
            await finish.Task.WaitAsync(token);
        };
        var flow = h.Machine.RunFlowAsync(h.Crane, task, default);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(h.Source.CurrentWorkpiece);
            Assert.Equal(LineState.PausedByFault, h.Line.State);
            Assert.True(h.Row.IsHeld(task.TaskId));
            Assert.Equal(task.TaskId, h.Reservations.Snapshot()[h.Source.Id]);
            Assert.Same(task, h.Crane.Runtime.CurrentTask);
            Assert.False(flow.IsCompleted);
        }
        finally
        {
            finish.TrySetResult();
            await flow.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(new[] { "DataSentDone=0", "LoadDone=0", "UnloadDone=0" }, h.SourceDriver.Writes.TakeLast(3));
        Assert.Empty(h.TargetDriver.Writes);
        Assert.Equal(WorkpieceState.Discarded, wp.Runtime.TaskState);
        Assert.False(h.Reservations.IsReserved(h.Source.Id));
        Assert.False(h.Row.IsHeld(task.TaskId));
    }

    /// <summary>清理某点失败或整台超时只留日志，不能替换原故障、阻塞工件弃板或资源释放。</summary>
    /// <param name="timeout">是否模拟清理期限耗尽，否则只让首个点写失败。</param>
    /// <returns>异步流程验证任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureCleanup_BestEffortCannotBlockOriginalAbort(bool timeout)
    {
        var h = new Harness(new BoringMachine(1, "源", 1000), new BoringMachine(2, "目标", 2000), 1000);
        h.Source.Signals = TestSignals.Of(("RequestUnload", true));
        h.Config.Options.Collection.CommunicationTimeoutMs = 100;
        var (wp, task) = h.NewTask();
        h.Source.CurrentWorkpiece = wp;
        h.CraneDriver.MagnetException = new IOException("原充磁故障");
        h.SourceDriver.OnWrite = (point, _, token) => point != "DataSentDone" ? Task.CompletedTask
            : timeout ? Task.Delay(Timeout.Infinite, token) : Task.FromException(new IOException("清理失败"));
        await h.Machine.RunFlowAsync(h.Crane, task, default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkpieceState.Discarded, wp.Runtime.TaskState);
        Assert.Null(h.Source.CurrentWorkpiece);
        Assert.Empty(h.TargetDriver.Writes);
        Assert.False(h.Row.IsHeld(task.TaskId));
        Assert.False(h.Reservations.IsReserved(h.Source.Id));
        Assert.Contains(h.Logger.Messages, m => m.Contains(timeout ? "异常握手清理失败或超时" : "握手位 DataSentDone 复位失败"));
        Assert.Contains(h.Logger.Messages, m => m.Contains("任务丢弃") && m.Contains("原充磁故障"));
        if (!timeout) Assert.Contains("UnloadDone=0", h.SourceDriver.Writes);
    }

    /// <summary>目标放料失败时尚无工件引用仍清其握手；源已释放/已有新工件时不再写源。</summary>
    /// <param name="foreignTarget">目标是否已换成其他工件。</param>
    /// <param name="foreignReservation">目标预约是否已交给其他任务。</param>
    /// <returns>异步流程验证任务。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task FailureCleanup_OnlyClearsOwnedTargetAfterPlaceStarted(bool foreignTarget, bool foreignReservation)
    {
        var h = new Harness(new BoringMachine(1, "源", 1000), new BoringMachine(2, "目标", 2000), 1000);
        h.Source.Signals = TestSignals.Of(("RequestUnload", true));
        h.Target.Signals = TestSignals.Of(("RequestData", true), ("RequestLoad", true));
        var (wp, task) = h.NewTask();
        h.Source.CurrentWorkpiece = wp;
        var other = new WorkpieceTask(new WorkpieceInfo());
        var nextTaskId = Guid.NewGuid();
        var sourceWritesAtFailure = 0;
        h.TargetDriver.OnWrite = (point, value, _) =>
        {
            if (point == "DataSentDone" && Equals(value, 1))
            {
                Assert.Null(h.Target.CurrentWorkpiece);
                h.Source.CurrentWorkpiece = other;
                sourceWritesAtFailure = h.SourceDriver.Writes.Count;
                if (foreignTarget) h.Target.CurrentWorkpiece = other;
                if (foreignReservation)
                {
                    h.Reservations.Release(task.TaskId);
                    Assert.True(h.Reservations.TryReserve(nextTaskId, 99, h.Target.Id));
                }
                throw new IOException("目标参数完成通知失败");
            }
            return Task.CompletedTask;
        };
        await h.Machine.RunFlowAsync(h.Crane, task, default);
        Assert.Same(other, h.Source.CurrentWorkpiece);
        Assert.Equal(sourceWritesAtFailure, h.SourceDriver.Writes.Count);
        Assert.Equal(foreignTarget || foreignReservation ? 1 : 2, h.TargetDriver.Writes.Count(w => w == "DataSentDone=0"));
        Assert.Same(foreignTarget ? other : null, h.Target.CurrentWorkpiece);
        Assert.Equal(WorkpieceState.Discarded, wp.Runtime.TaskState);
        Assert.False(h.Row.IsHeld(task.TaskId));
        if (foreignReservation) Assert.Equal(nextTaskId, h.Reservations.Snapshot()[h.Target.Id]);
    }

    /// <summary>源上方微调失败仍属充磁前故障，保留源设备上的工件，不发送下料完成。</summary>
    /// <returns>异步状态机验证任务。</returns>
    [Fact]
    public async Task SourceRefinementFailure_PreservesSourceWorkpieceBeforeMagnet()
    {
        var h = new Harness(new BoringMachine(1, "双头镗", 1000), new BoringMachine(2, "目标", 2000), 1000);
        h.Source.Signals = TestSignals.Of(("RequestUnload", true));
        var (wp, task) = h.NewTask();
        h.Source.CurrentWorkpiece = wp;
        wp.Runtime.CurrentLocation = WorkpieceLocation.AtDevice(h.Source.Id);
        wp.Runtime.CurrentProcess = ProcessType.Boring;
        wp.Runtime.QueueOrder = 7;
        h.CraneDriver.OnReadStatus = _ => Task.FromResult<CraneStatusDto?>(new() { XEncoder = 1010 });
        h.CraneDriver.OnRelativeMove = (_, _, _, _) => Task.FromResult(
            CraneCommandResult.Fail(CraneCommandCode.Timeout, "模拟源微调运动失败"));

        await h.Machine.RunFlowAsync(h.Crane, task, default);

        Assert.Equal("REL(-10,0,0)", Assert.Single(h.CraneDriver.Actions.Where(action => action.StartsWith("REL("))));
        Assert.Same(wp, h.Source.CurrentWorkpiece);
        Assert.Null(h.Target.CurrentWorkpiece);
        Assert.Null(h.Crane.CurrentWorkpiece);
        Assert.Equal(WorkpieceLocation.AtDevice(h.Source.Id), wp.Runtime.CurrentLocation);
        Assert.Equal(ProcessType.Boring, wp.Runtime.CurrentProcess);
        Assert.Equal(7, wp.Runtime.QueueOrder);
        Assert.Equal(WorkpieceState.Waiting, wp.Runtime.TaskState);
        Assert.Empty(h.SourceDriver.Writes);
        Assert.Empty(h.TargetDriver.Writes);
        Assert.Equal(LineState.PausedByFault, h.Line.State);
        Assert.False(h.Row.IsHeld(task.TaskId));
        Assert.False(h.Reservations.IsReserved(h.Source.Id));
        Assert.False(h.Reservations.IsReserved(h.Target.Id));
    }

    /// <summary>目标XY检查在清握手、释放源预约、缩路权之前，失败按原充磁后策略弃板；收尾不再检查。</summary>
    /// <param name="failTarget">是否让目标精定位失败。</param>
    /// <returns>异步状态机验证任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TargetRefinement_PrecedesCleanupAndRetainsOriginalFailurePolicy(bool failTarget)
    {
        var h = new Harness(new BoringMachine(1, "双头镗", 1000), new BoringMachine(2, "目标", 2000), 1000);
        h.Source.Signals = TestSignals.Of(("RequestUnload", true));
        h.Target.Signals = TestSignals.Of(("RequestData", true), ("RequestLoad", true));
        var (wp, task) = h.NewTask();
        h.Source.CurrentWorkpiece = wp;
        wp.Runtime.CurrentLocation = WorkpieceLocation.AtDevice(h.Source.Id);
        var targetChecked = false;
        h.CraneDriver.OnReadStatus = _ =>
        {
            var status = h.CraneDriver.Status;
            if (status?.XEncoder == 2000)
            {
                Assert.False(targetChecked);
                targetChecked = true;
                Assert.Contains("UnloadDone=1", h.SourceDriver.Writes);
                Assert.DoesNotContain("UnloadDone=0", h.SourceDriver.Writes);
                Assert.Empty(h.TargetDriver.Writes);
                Assert.True(h.Reservations.IsReserved(h.Source.Id));
                Assert.True(h.Reservations.IsReserved(h.Target.Id));
                Assert.Contains(h.Row.Snapshot(), interval => interval.Min < 1000 && interval.Max > 2000);
                return Task.FromResult<CraneStatusDto?>(new() { XEncoder = 2010 });
            }
            return Task.FromResult(status);
        };
        h.CraneDriver.OnRelativeMove = (_, _, _, _) => Task.FromResult(failTarget
            ? CraneCommandResult.Fail(CraneCommandCode.Timeout, "模拟目标微调运动失败")
            : CraneCommandResult.Ok(CraneCommandCode.InPosition, "普通运动到位"));
        await h.Machine.RunFlowAsync(h.Crane, task, default);
        Assert.True(targetChecked);
        Assert.Equal("REL(-10,0,0)", Assert.Single(h.CraneDriver.Actions.Where(action => action.StartsWith("REL("))));
        Assert.Equal(failTarget ? WorkpieceState.Discarded : WorkpieceState.Waiting, wp.Runtime.TaskState);
        if (failTarget)
        {
            Assert.Empty(h.TargetDriver.Writes);
            Assert.Equal(LineState.PausedByFault, h.Line.State);
        }
        else Assert.Contains("LoadDone=1", h.TargetDriver.Writes);
    }

    /// <summary>测试装配：真实注册表/管理器 + 打桩驱动与配置。</summary>
    private sealed class Harness
    {
        public DeviceRegistry Devices { get; } = new();
        public DriverRegistry Drivers { get; } = new();
        public RuntimeTaskManager Tasks { get; } = new(new TestLogger(), TestProcessPlans.Create());
        public CraneRegistry Cranes { get; } = new();
        public RightOfWayManager Row { get; }
        public ResourceReservationManager Reservations { get; }
        public LineControlService Line { get; }
        public AlarmManager Alarms { get; }
        public FakeConfigurationStore Store { get; } = new();
        public FakeConfigService Config { get; } = new();
        /// <summary>保留实际流程日志，核对尽力清理失败不会覆盖原始故障。</summary>
        public TestLogger Logger { get; } = new();
        public CraneStateMachine Machine { get; }
        public Crane Crane { get; }
        public FakeCraneDriver CraneDriver { get; }
        public DeviceBase Source { get; }
        public DeviceBase Target { get; }
        public FakeDeviceDriver SourceDriver { get; }
        public FakeDeviceDriver TargetDriver { get; }

        /// <summary>装配真实状态机和桩驱动；默认启动线路，非运行入口测试可显式保留Stopped。</summary>
        /// <param name="source">本次搬运源设备。</param>
        /// <param name="target">本次搬运目标设备。</param>
        /// <param name="handshakeTimeoutMs">设备握手等待期限，单位毫秒。</param>
        /// <param name="startLine">是否按正常搬运前提启动线路。</param>
        /// <param name="craneId">天车号，默认1，用于验证不同天车的收尾策略。</param>
        public Harness(DeviceBase source, DeviceBase target, int handshakeTimeoutMs, bool startLine = true, int craneId = 1)
        {
            Crane = new Crane(craneId, craneId, $"天车{craneId}")
            {
                SafeX = 0,
                SafeZ = 500,
                Width = 100,
                StartX = 0,
                EndX = 5000,
                IsEnabled = true
            };
            CraneDriver = new FakeCraneDriver { CraneId = craneId };
            Source = source;
            Target = target;
            var logger = Logger;
            Config.Options.StateMachine.HandshakeTimeoutMs = handshakeTimeoutMs;
            Config.Options.Crane.XyRefineSettleMs = 0;

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
        /// <param name="targetProcess">本次搬运的目标工序。</param>
        /// <returns>工件及其搬运任务。</returns>
        public (WorkpieceTask Wp, TransportTask Task) NewTask(ProcessType targetProcess = ProcessType.Boring)
        {
            var wp = Tasks.Add(new WorkpieceInfo { Diameter = 100, Length = 700 });
            var task = new TransportTask
            {
                WorkpieceRuntimeId = wp.Id,
                SourceDeviceId = Source.Id,
                TargetDeviceId = Target.Id,
                SourceX = Source.PositionX,
                TargetX = Target.PositionX,
                TargetProcess = targetProcess
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
        h.Target.Signals = TestSignals.Of(("RequestData", true), ("RequestLoad", true), ("LoadLockDone", true));
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
        h.Source.Signals = TestSignals.Of(("RequestUnload", true), ("UnloadReleaseDone", true));
        h.Target.Signals = TestSignals.Of(("RequestData", true), ("RequestLoad", true), ("LoadLockDone", true));
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
        h.Target.Signals = TestSignals.Of(("RequestData", true), ("RequestLoad", true), ("LoadLockDone", true));
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
        h.Target.Signals = TestSignals.Of(("RequestData", true), ("RequestLoad", true), ("LoadLockDone", true));
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
        h.Target.Signals = TestSignals.Of(("RequestData", true), ("RequestLoad", true), ("LoadLockDone", true));
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

    /// <summary>收尾回安全位成功后才清目标握手；清理期间保留资源，正常完成或取消均沿用原状态处理。</summary>
    /// <param name="cancelMode">0正常完成，1人工中止，2宿主取消。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Finish_ClearsTargetAfterSafeMoveBeforeReleasingResources(int cancelMode)
    {
        var h = new Harness(new LoadRack(1, "上料架", 1000), new BoringMachine(2, "双头镗", 2000), 10000);
        h.Source.Signals = TestSignals.Of(("HasMaterial", true));
        h.Target.Signals = TestSignals.Of(("RequestData", true), ("RequestLoad", true));
        var (wp, task) = h.NewTask();
        using var host = new CancellationTokenSource();
        await using var execution = new CraneTaskExecution(task, host.Token);
        var safeMoveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowSafeMove = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clearStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowClear = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var safeMoveFinished = false;
        h.CraneDriver.OnAbsoluteMove = async (x, token) =>
        {
            if (x == h.Crane.SafeX)
            {
                safeMoveStarted.TrySetResult();
                await allowSafeMove.Task.WaitAsync(token);
                safeMoveFinished = true;
            }
            return CraneCommandResult.Ok(CraneCommandCode.InPosition, "ok");
        };
        h.TargetDriver.OnWrite = async (point, value, token) =>
        {
            // 中转清理发生在LoadDone=1之前；只阻塞放料完成后的第二次清理。
            if (point == "DataSentDone" && value is int number && number == 0
                && h.TargetDriver.Writes.Contains("LoadDone=1"))
            {
                Assert.True(safeMoveFinished);
                clearStarted.TrySetResult();
                await allowClear.Task.WaitAsync(token);
            }
        };
        var flow = h.Machine.RunFlowAsync(h.Crane, task, execution.Token, execution);
        var competitor = Guid.NewGuid();
        try
        {
            await safeMoveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(h.TargetDriver.Writes.Where(w => w == "DataSentDone=0"));
            Assert.False(clearStarted.Task.IsCompleted);
            allowSafeMove.TrySetResult();
            await clearStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(h.Reservations.IsReserved(h.Target.Id));
            Assert.True(h.Row.IsHeld(task.TaskId));
            Assert.Same(task, h.Crane.Runtime.CurrentTask);
            Assert.Equal(WorkpieceState.Running, wp.Runtime.TaskState);
            Assert.Same(wp, h.Target.CurrentWorkpiece);
            Assert.False(h.Reservations.TryReserve(competitor, 99, h.Target.Id));

            if (cancelMode == 0)
            {
                allowClear.TrySetResult();
                await flow.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(new[] { "DataSentDone=0", "LoadDone=0", "UnloadDone=0" }, h.TargetDriver.Writes.TakeLast(3));
                Assert.Same(wp, h.Target.CurrentWorkpiece);
                Assert.Equal(WorkpieceLocation.AtDevice(h.Target.Id), wp.Runtime.CurrentLocation);
                Assert.Equal(WorkpieceState.Waiting, wp.Runtime.TaskState);
                Assert.Equal(LineState.Running, h.Line.State);
            }
            else
            {
                if (cancelMode == 1)
                {
                    var request = typeof(CraneTaskExecution).GetMethod("TryRequestManualAbort",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                    Assert.True((bool)request.Invoke(execution, new object[] { (Action)h.Line.EnterFaultPause })!);
                }
                else host.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flow.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(WorkpieceState.Discarded, wp.Runtime.TaskState);
                Assert.Null(h.Target.CurrentWorkpiece);
                Assert.Equal(LineState.PausedByFault, h.Line.State);
            }
            Assert.True(h.Crane.IsIdle);
            Assert.False(h.Row.IsHeld(task.TaskId));
            Assert.False(h.Reservations.IsReserved(h.Target.Id));
            Assert.True(h.Reservations.TryReserve(competitor, 99, h.Target.Id));
        }
        finally
        {
            allowSafeMove.TrySetResult();
            allowClear.TrySetResult();
            try { await flow.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (execution.Token.IsCancellationRequested) { }
            h.Reservations.Release(competitor);
        }
    }

    /// <summary>仅1号车送研磨机或打号机跳过收尾回位，所有分支仍清握手、释放资源并提交工件状态。</summary>
    /// <param name="craneId">实际天车号。</param>
    /// <param name="targetType">目标设备类型。</param>
    /// <param name="skipReturn">是否应跳过收尾回位。</param>
    /// <returns>异步流程验证任务。</returns>
    [Theory]
    [InlineData(1, DeviceType.GrinderMachine, true)]
    [InlineData(1, DeviceType.MarkerMachine, true)]
    [InlineData(1, DeviceType.BoringMachine, false)]
    [InlineData(1, DeviceType.UnloadRack, false)]
    [InlineData(2, DeviceType.GrinderMachine, false)]
    [InlineData(2, DeviceType.MarkerMachine, false)]
    [InlineData(2, DeviceType.BoringMachine, false)]
    [InlineData(2, DeviceType.UnloadRack, false)]
    public async Task Finish_ReturnsToSafePositionExceptCraneOneGrindingOrMarking(int craneId, DeviceType targetType, bool skipReturn)
    {
        DeviceBase target = targetType switch
        {
            DeviceType.GrinderMachine => new GrinderMachine(2, "研磨机", 2000),
            DeviceType.MarkerMachine => new MarkerMachine(2, "打号机", 2000),
            DeviceType.BoringMachine => new BoringMachine(2, "双头镗", 2000),
            _ => new UnloadRack(2, "下料架", 2000)
        };
        var h = new Harness(new LoadRack(1, "上料架", 1000), target, 10000, craneId: craneId);
        // 显示序号不决定设备身份，确保分支依据实际天车Id。
        h.Crane.No = craneId == 1 ? 2 : 1;
        h.Source.Signals = TestSignals.Of(("HasMaterial", true));
        target.Signals = TestSignals.Of(("RequestData", true), ("RequestLoad", true), ("LoadLockDone", true),
            ("Busy", false), ("HasMaterial", false));
        var process = targetType switch
        {
            DeviceType.GrinderMachine => ProcessType.Grinding,
            DeviceType.MarkerMachine => ProcessType.Marking,
            DeviceType.BoringMachine => ProcessType.Boring,
            _ => ProcessType.Unloading
        };
        var (wp, task) = h.NewTask(process);
        var clearSnapshots = new List<(string LastMove, bool HoldsResources, WorkpieceState State)>();
        h.TargetDriver.OnWrite = (_, value, _) =>
        {
            if (Equals(value, 0) && h.TargetDriver.Writes.Contains("LoadDone=1"))
            {
                // 清理方法逐点容错，先记录现场，结束后断言，避免测试断言被当通信失败吞掉。
                clearSnapshots.Add((h.CraneDriver.Actions.Last(),
                    h.Row.IsHeld(task.TaskId) && h.Reservations.IsReserved(target.Id), wp.Runtime.TaskState));
            }
            return Task.CompletedTask;
        };

        await h.Machine.RunFlowAsync(h.Crane, task, CancellationToken.None);

        Assert.Equal(!skipReturn, h.CraneDriver.Actions.Contains("MOVE(0,0,500)"));
        Assert.Equal(targetType != DeviceType.UnloadRack, clearSnapshots.Count > 0);
        Assert.All(clearSnapshots, snapshot =>
        {
            Assert.Equal(skipReturn ? "MOVE(2000,0,500)" : "MOVE(0,0,500)", snapshot.LastMove);
            Assert.True(snapshot.HoldsResources);
            Assert.Equal(WorkpieceState.Running, snapshot.State);
        });
        Assert.Contains("MAGNET_OFF", h.CraneDriver.Actions);
        Assert.Contains("MOVE(2000,0,500)", h.CraneDriver.Actions);
        Assert.Same(wp, target.CurrentWorkpiece);
        Assert.Equal(WorkpieceLocation.AtDevice(target.Id), wp.Runtime.CurrentLocation);
        Assert.Equal(process == ProcessType.Unloading ? WorkpieceState.Completed : WorkpieceState.Waiting, wp.Runtime.TaskState);
        Assert.Equal(process == ProcessType.Unloading, wp.Runtime.CompletedAtUtc.HasValue);
        Assert.True(h.Crane.IsIdle);
        Assert.Null(h.Crane.Runtime.LockedInterval);
        Assert.False(h.Row.IsHeld(task.TaskId));
        Assert.False(h.Reservations.IsReserved(target.Id));
        Assert.Equal(LineState.Running, h.Line.State);
    }

    /// <summary>回安全位失败改由异常分支清目标；正常收尾单点写失败仍尽力清，不改变已交付工件。</summary>
    /// <param name="failSafeMove">是否让回安全位失败，否则仅让收尾首个握手写入失败。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Finish_PreservesExistingMotionAndBestEffortFailurePolicies(bool failSafeMove)
    {
        var h = new Harness(new LoadRack(1, "上料架", 1000), new BoringMachine(2, "双头镗", 2000), 10000);
        h.Source.Signals = TestSignals.Of(("HasMaterial", true));
        h.Target.Signals = TestSignals.Of(("RequestData", true), ("RequestLoad", true));
        var (wp, task) = h.NewTask();
        if (failSafeMove) h.CraneDriver.FailAbsoluteMoveX = (int)h.Crane.SafeX;
        h.TargetDriver.OnWrite = (point, value, _) =>
        {
            if (!failSafeMove && point == "DataSentDone" && value is int number && number == 0
                && h.TargetDriver.Writes.Contains("LoadDone=1"))
                throw new IOException("模拟收尾握手复位失败");
            return Task.CompletedTask;
        };

        await h.Machine.RunFlowAsync(h.Crane, task, CancellationToken.None);

        Assert.Equal(2, h.TargetDriver.Writes.Count(w => w == "DataSentDone=0"));
        Assert.Equal(2, h.TargetDriver.Writes.Count(w => w == "LoadDone=0"));
        Assert.Equal(2, h.TargetDriver.Writes.Count(w => w == "UnloadDone=0"));
        Assert.Equal(failSafeMove ? WorkpieceState.Discarded : WorkpieceState.Waiting, wp.Runtime.TaskState);
        Assert.Same(failSafeMove ? null : wp, h.Target.CurrentWorkpiece);
        Assert.Equal(failSafeMove ? LineState.PausedByFault : LineState.Running, h.Line.State);
        Assert.True(h.Crane.IsIdle);
        Assert.False(h.Row.IsHeld(task.TaskId));
        Assert.False(h.Reservations.IsReserved(h.Target.Id));
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
        h.Source.Signals = TestSignals.Of(("RequestUnload", true), ("UnloadReleaseDone", true));
        h.Target.Signals = TestSignals.Of(("RequestData", true), ("RequestLoad", true), ("LoadLockDone", true));
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
        h.Target.Signals = TestSignals.Of(("RequestData", true), ("RequestLoad", true), ("LoadLockDone", true));
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

        h.Source.Signals = TestSignals.Of(("RequestUnload", true), ("UnloadReleaseDone", true));
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
        h.Source.Signals = TestSignals.Of(("RequestUnload", true), ("UnloadReleaseDone", true));
        h.Target.Signals = TestSignals.Of(("RequestData", true), ("RequestLoad", true), ("LoadLockDone", true));
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
        Assert.Equal(communicationFailure ? 1 : 4, h.CraneDriver.HasPlateReadCount);
        Assert.Equal(communicationFailure ? 0 : 3, h.CraneDriver.Actions.Count(action => action == "REL(0,0,5)"));

        h.CraneDriver.Status = new() { HasPlate = true };
        h.CraneDriver.HasPlateException = null;
        h.Line.Start();
        await h.Machine.RunFlowAsync(h.Crane, task, CancellationToken.None);
        Assert.Null(h.Source.CurrentWorkpiece);
        Assert.Same(wp, h.Target.CurrentWorkpiece);
        Assert.Equal(WorkpieceLocation.AtDevice(h.Target.Id), wp.Runtime.CurrentLocation);
    }

    /// <summary>五类来源共用配置化Z补降，检测到板后才充磁，设备内存交接仍在完整Pick返回后发生。</summary>
    /// <param name="sourceType">支持取料的源设备类型。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(DeviceType.LoadRack)]
    [InlineData(DeviceType.BoringMachine)]
    [InlineData(DeviceType.LatheMachine)]
    [InlineData(DeviceType.GrinderMachine)]
    [InlineData(DeviceType.MarkerMachine)]
    public async Task PlateRetry_AllPickHandlersShareConfiguredDrop(DeviceType sourceType)
    {
        DeviceBase source = sourceType switch
        {
            DeviceType.LoadRack => new LoadRack(1, "源上料架", 1000),
            DeviceType.BoringMachine => new BoringMachine(1, "源双头镗", 1000),
            DeviceType.LatheMachine => new LatheMachine(1, "源斜床", 1000),
            DeviceType.GrinderMachine => new GrinderMachine(1, "源研磨机", 1000),
            DeviceType.MarkerMachine => new MarkerMachine(1, "源打号机", 1000),
            _ => throw new InvalidOperationException("测试设备类型不支持")
        };
        var h = new Harness(source, new BoringMachine(2, "目标双头镗", 2000), 10000);
        h.Config.Options.StateMachine.PickPlateRetryCount = 1;
        h.Config.Options.StateMachine.PickPlateStepMm = 2;
        h.Source.Signals = TestSignals.Of(("HasMaterial", true), ("RequestUnload", true),
            ("TailstockOpened", true), ("UnloadReleaseDone", true), ("ProcessDone", true));
        h.Target.Signals = TestSignals.Of(("RequestData", true), ("RequestLoad", true), ("LoadLockDone", true));
        h.CraneDriver.OnReadHasPlateAsync = _ => Task.FromResult(h.CraneDriver.HasPlateReadCount > 1);
        var (wp, task) = h.NewTask();
        if (sourceType != DeviceType.LoadRack)
        {
            source.CurrentWorkpiece = wp;
            wp.Runtime.CurrentLocation = WorkpieceLocation.AtDevice(source.Id);
        }

        await h.Machine.RunFlowAsync(h.Crane, task, CancellationToken.None);

        Assert.Equal(LineState.Running, h.Line.State);
        Assert.Equal(2, h.CraneDriver.HasPlateReadCount);
        Assert.Equal(1, h.CraneDriver.Actions.Count(action => action == "REL(0,0,2)"));
        Assert.True(h.CraneDriver.Actions.IndexOf("REL(0,0,2)") < h.CraneDriver.Actions.IndexOf("MAGNET_ON"));
        Assert.Null(source.CurrentWorkpiece);
        Assert.Same(wp, h.Target.CurrentWorkpiece);
        Assert.Equal(WorkpieceLocation.AtDevice(h.Target.Id), wp.Runtime.CurrentLocation);
        Assert.Equal(WorkpieceState.Waiting, wp.Runtime.TaskState);
        Assert.True(h.Crane.IsIdle);
        Assert.False(h.Row.IsHeld(task.TaskId));
        Assert.False(h.Reservations.IsReserved(source.Id));
        Assert.False(h.Reservations.IsReserved(h.Target.Id));
        Assert.Empty(h.Alarms.Store.Snapshot());
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
        h.Source.Signals = TestSignals.Of(("RequestUnload", true), ("UnloadReleaseDone", true));
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
        Assert.Equal(new[] { "DataSentDone=0", "LoadDone=0", "UnloadDone=0" }, h.SourceDriver.Writes);
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
        h.Source.Signals = TestSignals.Of(("RequestUnload", true), ("UnloadReleaseDone", true));
        h.Target.Signals = TestSignals.Of(("RequestData", true), ("RequestLoad", true), ("LoadLockDone", true));
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

    /// <summary>真实中转按源/目标角色复位打号机握手，取放完成信号和后续内存交接保持原顺序。</summary>
    /// <param name="markerSource">打号机是否作为源设备；否则作为目标设备。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Transit_ResetsMarkerHandshakeForBothRoles(bool markerSource)
    {
        DeviceBase source = markerSource ? new MarkerMachine(1, "源打号机", 1000) : new LoadRack(1, "上料架", 1000);
        DeviceBase target = markerSource ? new UnloadRack(2, "下料架", 2000) : new MarkerMachine(2, "目标打号机", 2000);
        var h = new Harness(source, target, 10000);
        source.Signals = TestSignals.Of(("HasMaterial", true), ("ProcessDone", true));
        target.Signals = TestSignals.Of(("HasMaterial", false), ("Busy", false));
        var (wp, task) = h.NewTask(markerSource ? ProcessType.Unloading : ProcessType.Marking);
        if (markerSource)
        {
            source.CurrentWorkpiece = wp;
            wp.Runtime.CurrentLocation = WorkpieceLocation.AtDevice(source.Id);
        }
        var markerDriver = markerSource ? h.SourceDriver : h.TargetDriver;
        markerDriver.Signals["LoadDone"] = 1;
        markerDriver.Signals["UnloadDone"] = 1;

        await h.Machine.RunFlowAsync(h.Crane, task, CancellationToken.None);

        if (markerSource)
        {
            Assert.Equal(new[] { "UnloadDone=1", "LoadDone=0", "UnloadDone=0" }, markerDriver.Writes);
        }
        else
        {
            Assert.Equal(new[] { "LoadDone=0", "UnloadDone=0" }, markerDriver.Writes.Take(2));
            Assert.StartsWith("PrintContent=", markerDriver.Writes[2]);
            Assert.Equal(new[] { "LoadDone=1", "LoadDone=0", "UnloadDone=0" }, markerDriver.Writes.TakeLast(3));
        }
        Assert.Same(wp, target.CurrentWorkpiece);
        Assert.Null(source.CurrentWorkpiece);
        Assert.Equal(markerSource ? WorkpieceState.Completed : WorkpieceState.Waiting, wp.Runtime.TaskState);
        Assert.Equal(LineState.Running, h.Line.State);
        Assert.Equal(CraneState.Idle, h.Crane.Runtime.State);
        Assert.Null(h.Crane.Runtime.CurrentTask);
        Assert.False(h.Row.IsHeld(task.TaskId));
        Assert.False(h.Reservations.IsReserved(source.Id));
        Assert.False(h.Reservations.IsReserved(target.Id));
    }

    /// <summary>双头镗取放完成写入失败仍按充磁后弃板，沿用原资源及内存清理。</summary>
    /// <param name="pick">是否在取料完成时模拟写入失败，否则放料完成。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoringCompletionWriteFailure_UsesExistingDiscardCleanup(bool pick)
    {
        DeviceBase source = pick ? new BoringMachine(1, "源双头镗", 1000) : new LoadRack(1, "上料架", 1000);
        DeviceBase target = pick ? new UnloadRack(2, "下料架", 2000) : new BoringMachine(2, "目标双头镗", 2000);
        var h = new Harness(source, target, 0);
        source.Signals = TestSignals.Of(("HasMaterial", true), ("RequestUnload", true));
        target.Signals = TestSignals.Of(("HasMaterial", false), ("RequestData", true), ("RequestLoad", true));
        var boringDriver = pick ? h.SourceDriver : h.TargetDriver;
        var completionPoint = pick ? "UnloadDone" : "LoadDone";
        boringDriver.OnWrite = (point, value, _) => point == completionPoint && Convert.ToInt32(value) == 1
            ? Task.FromException(new IOException("模拟完成信号写入失败")) : Task.CompletedTask;
        var (wp, task) = h.NewTask();
        if (pick)
        {
            source.CurrentWorkpiece = wp;
            wp.Runtime.CurrentLocation = WorkpieceLocation.AtDevice(source.Id);
        }

        await h.Machine.RunFlowAsync(h.Crane, task, CancellationToken.None);

        Assert.Equal(WorkpieceState.Discarded, wp.Runtime.TaskState);
        Assert.Equal(LineState.PausedByFault, h.Line.State);
        Assert.Null(source.CurrentWorkpiece);
        Assert.Null(target.CurrentWorkpiece);
        Assert.Null(h.Crane.CurrentWorkpiece);
        Assert.True(h.Crane.IsIdle);
        Assert.False(h.Row.IsHeld(task.TaskId));
        Assert.False(h.Reservations.IsReserved(source.Id));
        Assert.False(h.Reservations.IsReserved(target.Id));
        Assert.Equal(!pick, h.CraneDriver.Actions.Contains("MAGNET_OFF"));
        Assert.Contains(completionPoint + "=1", boringDriver.Writes);
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
