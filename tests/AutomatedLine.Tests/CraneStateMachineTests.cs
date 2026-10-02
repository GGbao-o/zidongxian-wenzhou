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
        public RuntimeTaskManager Tasks { get; } = new();
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

    /// <summary>统一异常：源未就绪超时（充磁前）→ 释放资源 + 清理任务 + 暂停线路（不再区分充磁前后）。</summary>
    [Fact]
    public async Task SourceNotReady_Aborts_Releases_And_Pauses()
    {
        var h = new Harness(new LoadRack(1, "上料架", 1000), new BoringMachine(2, "双头镗", 2000), handshakeTimeoutMs: 0);
        // 上料架无板 → 取料等待超时
        var (wp, task) = h.NewTask();

        await h.Machine.RunFlowAsync(h.Crane, task, CancellationToken.None);

        Assert.Null(h.Tasks.Get(wp.Id));                        // 任务已清理
        Assert.Equal(LineState.PausedByFault, h.Line.State);    // 已暂停
        Assert.Null(h.Crane.Runtime.CurrentTask);
        Assert.False(h.Row.IsHeld(task.TaskId));                // 路权已释放
        Assert.False(h.Reservations.IsReserved(h.Source.Id));   // 源预约已释放
        Assert.False(h.Reservations.IsReserved(h.Target.Id));   // 目标预约已释放
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

        Assert.Null(h.Tasks.Get(wp.Id));                        // 已弃板
        Assert.Equal(LineState.PausedByFault, h.Line.State);    // 已暂停
        Assert.Null(h.Crane.Runtime.CurrentTask);
        Assert.False(h.Reservations.IsReserved(h.Source.Id));
        Assert.False(h.Reservations.IsReserved(h.Target.Id));
    }
}
