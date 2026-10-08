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
/// 正常一条、acquire 失败回滚、充磁前回滚、充磁后中止。
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

        public Harness(DeviceBase source, DeviceBase target, int handshakeTimeoutMs)
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

            Machine = new CraneStateMachine(Devices, Drivers, Tasks, Row, Reservations, abort, handlers, Store, Config, logger);

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
                h.CraneDriver.MagnetResult = CraneCommandResult.Fail(CraneCommandCode.Communication, "充磁命令失败");
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
