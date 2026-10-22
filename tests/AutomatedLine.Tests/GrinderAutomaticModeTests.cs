using System.Net;
using System.Net.Sockets;
using System.Reflection;
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Collection;
using AutomatedLine.Application.Generation;
using AutomatedLine.Application.Line;
using AutomatedLine.Application.Reservations;
using AutomatedLine.Application.RightOfWay;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Application.Scheduling;
using AutomatedLine.Domain.Cranes;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Domain.Routing;
using AutomatedLine.Domain.Signals;
using AutomatedLine.Domain.Workpieces;
using AutomatedLine.Infrastructure.Drivers;
using AutomatedLine.Tests.Fakes;

namespace AutomatedLine.Tests;

/// <summary>验证研磨机模式采集、候选生成及旧候选入队前复核，不访问现场PLC。</summary>
public class GrinderAutomaticModeTests
{
    /// <summary>所有非1及未知模式均禁止生成，模式恢复为1后同一工件可继续生成。</summary>
    /// <param name="grinderSource">研磨机是否为源，否则为目标。</param>
    /// <param name="mode">PLC模式原值；null表示点位缺失。</param>
    /// <param name="allowed">预期是否生成候选。</param>
    [Theory]
    [InlineData(true, 1d, true)]
    [InlineData(false, 1d, true)]
    [InlineData(true, 0d, false)]
    [InlineData(false, 0d, false)]
    [InlineData(true, 2d, false)]
    [InlineData(false, 2d, false)]
    [InlineData(true, null, false)]
    [InlineData(false, null, false)]
    [InlineData(true, 1.2d, false)]
    [InlineData(false, 1.2d, false)]
    public void Generator_RequiresModeExactlyOne(bool grinderSource, double? mode, bool allowed)
    {
        var h = new Harness(grinderSource);
        h.Grinder.Signals = Signals(mode);
        h.Generate();
        Assert.Equal(allowed ? 1 : 0, h.Pool.Snapshot().Count);
        Assert.Equal(WorkpieceState.Waiting, h.Workpiece.Runtime.TaskState);
        if (!allowed)
        {
            Assert.Contains(h.Logger.Messages, m => m.Contains("12063") && m.Contains("手自动模式"));
            h.Grinder.Signals = Signals(1);
            h.Generate();
            Assert.Single(h.Pool.Snapshot());
        }
    }

    /// <summary>旧候选生成后模式变化，入队前拒绝；恢复后不用重建或丢弃工件即可派发。</summary>
    /// <param name="grinderSource">模式变化的研磨机是否为源。</param>
    /// <param name="mode">候选生成后发布的模式值；null表示点位丢失。</param>
    /// <returns>异步派发验证任务。</returns>
    [Theory]
    [InlineData(true, 0d)]
    [InlineData(false, 0d)]
    [InlineData(true, 2d)]
    [InlineData(false, 2d)]
    [InlineData(true, null)]
    [InlineData(false, null)]
    public async Task Scheduler_RechecksModeForOldCandidate(bool grinderSource, double? mode)
    {
        var h = new Harness(grinderSource);
        h.Generate();
        Assert.Single(h.Pool.Snapshot());
        var location = h.Workpiece.Runtime.CurrentLocation;
        var order = h.Workpiece.Runtime.QueueOrder;
        h.Grinder.Signals = Signals(mode);
        await h.Dispatch();
        Assert.False(h.Channels.For(1).Reader.TryRead(out _));
        Assert.Equal(WorkpieceState.Waiting, h.Workpiece.Runtime.TaskState);
        Assert.Equal(location, h.Workpiece.Runtime.CurrentLocation);
        Assert.Equal(order, h.Workpiece.Runtime.QueueOrder);
        Assert.Same(h.Workpiece, h.Tasks.Get(h.Workpiece.Id));
        Assert.True(h.Line.IsRunning);
        Assert.Null(h.Crane.Runtime.CurrentTask);
        Assert.False(h.Reservations.IsReserved(h.Grinder.Id));
        Assert.Contains(h.Logger.Messages, m => m.Contains("暂未派发") && m.Contains("12063"));
        h.Grinder.Signals = Signals(1);
        await h.Dispatch();
        Assert.True(h.Channels.For(1).Reader.TryRead(out var transport));
        Assert.Equal(h.Workpiece.Id, transport.WorkpieceRuntimeId);
        Assert.Equal(WorkpieceState.Assigned, h.Workpiece.Runtime.TaskState);
    }

    /// <summary>有效模式不能替代请求握手；坏质量、断线或未知值都不能冒充自动模式。</summary>
    [Fact]
    public void Rules_KeepHandshakeAndQualityRequirements()
    {
        var grinder = new GrinderMachine(2, "研磨机", 200);
        grinder.Signals = Signals(1) with { Quality = SignalQuality.Bad };
        Assert.False(DeviceHandshakeRules.CanProvide(grinder));
        Assert.False(DeviceHandshakeRules.CanReceive(grinder));
        grinder.Signals = Signals(1) with { ConnectionState = ConnectionState.Disconnected };
        Assert.False(DeviceHandshakeRules.IsAutomaticMode(grinder.Signals));
        grinder.Signals = TestSignals.Of(("AutoMode", 1d));
        Assert.False(DeviceHandshakeRules.CanProvide(grinder));
        Assert.False(DeviceHandshakeRules.CanReceive(grinder));
        grinder.Signals = TestSignals.Of(("AutoMode", true), ("RequestData", true));
        Assert.False(DeviceHandshakeRules.CanReceive(grinder));
        grinder.Signals = null;
        Assert.False(DeviceHandshakeRules.CanProvide(grinder));
        Assert.Contains("未知", DeviceHandshakeRules.AutomaticModeBlockReason(grinder)!);
        var boring = new BoringMachine(3, "双头镗", 300) { Signals = TestSignals.Of(("RequestData", true)) };
        Assert.True(DeviceHandshakeRules.CanReceive(boring));
    }

    /// <summary>一台研磨机非自动时仅排除该设备，其他自动且就绪的研磨机仍可生成候选。</summary>
    [Fact]
    public void Generator_OtherAutomaticGrinderRemainsAvailable()
    {
        var h = new Harness(false);
        h.Grinder.Signals = Signals(0);
        h.Devices.Register(new GrinderMachine(4, "研磨机02", 250)
        {
            Signals = Signals(1), MaxDiameter = 1000, MaxLength = 5000
        });
        h.Generate();
        Assert.Equal(4, Assert.Single(h.Pool.Snapshot()).Target.Id);
        Assert.True(h.Line.IsRunning);
        Assert.Equal(WorkpieceState.Waiting, h.Workpiece.Runtime.TaskState);
    }

    /// <summary>真实驱动通过FC03读取地址12063，保留Word原值，不使用非零布尔转换。</summary>
    /// <param name="mode">服务端返回的模式Word。</param>
    /// <returns>异步通信验证任务。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(65535)]
    public async Task Driver_ReadsModeAsNumericWord(int mode)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var points = DeviceModbusMaps.Grinder();
        var modePoint = Assert.Single(points.Where(p => p.Name == "AutoMode"));
        Assert.Equal(12063, modePoint.Address);
        Assert.False(modePoint.Writable);
        Assert.False(modePoint.IsBool);
        var driver = new ModbusDeviceDriver(2, "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
            1, points, new TestLogger());
        try
        {
            var read = driver.ReadAllSignalsAsync(deadline.Token);
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            var stream = peer.GetStream();
            foreach (var point in points.Where(p => !p.Writable))
            {
                var request = new byte[12];
                await stream.ReadExactlyAsync(request, deadline.Token);
                Assert.Equal(3, request[7]);
                Assert.Equal(point.Address, (request[8] << 8) | request[9]);
                Assert.Equal(1, (request[10] << 8) | request[11]);
                var value = point.Name == "AutoMode" ? mode : 0;
                await stream.WriteAsync(new byte[] { request[0], request[1], 0, 0, 0, 5, 1,
                    3, 2, (byte)(value >> 8), (byte)value }, deadline.Token);
            }
            var values = await read;
            Assert.Equal(9, values.Count);
            Assert.Equal((double)mode, Assert.IsType<double>(values["AutoMode"]));
        }
        finally
        {
            await driver.DisconnectAsync();
            listener.Stop();
        }
    }

    private static SignalSnapshot Signals(double? mode)
        => mode.HasValue
            ? TestSignals.Of(("AutoMode", mode.Value), ("RequestData", true), ("RequestUnload", true))
            : TestSignals.Of(("RequestData", true), ("RequestUnload", true));

    private sealed class Harness
    {
        public TestLogger Logger { get; } = new();
        public FakeConfigService Config { get; } = new();
        public DeviceRegistry Devices { get; } = new();
        public CandidateTaskPool Pool { get; } = new();
        public CraneTaskChannels Channels { get; } = new();
        public CraneRegistry Cranes { get; } = new();
        public Crane Crane { get; } = new(1, 1, "天车") { IsEnabled = true, StartX = 0, EndX = 1000, Width = 10 };
        public GrinderMachine Grinder { get; } = new(2, "研磨机", 200) { MaxDiameter = 1000, MaxLength = 5000 };
        public RuntimeTaskManager Tasks { get; }
        public ResourceReservationManager Reservations { get; }
        public LineControlService Line { get; }
        public WorkpieceTask Workpiece { get; }
        private readonly TransportTaskGenerator _generator;
        private readonly CraneScheduler _scheduler;

        public Harness(bool grinderSource)
        {
            Tasks = new RuntimeTaskManager(Logger, TestProcessPlans.Create());
            Reservations = new ResourceReservationManager(Logger);
            Line = new LineControlService(Logger);
            Workpiece = Tasks.Add(new WorkpieceInfo { PlateNo = "MODE", SerialNo = "1", Diameter = 100, Length = 700 });
            // 本夹具直接构造已启动场景，必须同时保留它启动时的研磨路线。
            Workpiece.Runtime.LockProcessPlan(new ProcessPlanSnapshot(ProcessRoute.CreateDefault().Nodes
                .Where(node => node.Process is ProcessType.Loading or ProcessType.Grinding or ProcessType.Unloading)));
            Workpiece.Runtime.TaskState = WorkpieceState.Waiting;
            Workpiece.Runtime.QueueOrder = 7;
            Grinder.Signals = Signals(1);
            Devices.Register(Grinder);
            if (grinderSource)
            {
                Workpiece.Runtime.CurrentLocation = WorkpieceLocation.AtDevice(Grinder.Id);
                Workpiece.Runtime.CurrentProcess = ProcessType.Grinding;
                Grinder.CurrentWorkpiece = Workpiece;
                Devices.Register(new UnloadRack(3, "下料架", 300)
                {
                    Signals = TestSignals.Of(("HasMaterial", false)), MaxDiameter = 1000, MaxLength = 5000
                });
            }
            else
            {
                Workpiece.Runtime.CurrentLocation = WorkpieceLocation.Queue;
                Devices.Register(new LoadRack(1, "上料架", 100) { Signals = TestSignals.Of(("HasMaterial", true)) });
            }
            Cranes.Register(Crane);
            _generator = new TransportTaskGenerator(Tasks, Devices, Pool, Reservations, Line, Config, Logger);
            _scheduler = new CraneScheduler(Pool, Channels, Cranes, new ReadyCrane(), new RightOfWayManager(Logger),
                Reservations, Line, Config, Logger);
            Line.Start();
        }
        public void Generate() => typeof(TransportTaskGenerator).GetMethod("GenerateOnce", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_generator, new object[] { false });
        public Task Dispatch() => (Task)typeof(CraneScheduler).GetMethod("DispatchOnceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_scheduler, new object[] { false, CancellationToken.None })!;
    }

    private sealed class ReadyCrane : ICraneStateProvider
    {
        public CraneStateSnapshot? GetSnapshot(int craneId) => null;
        public CraneStatusDto? GetFreshStatus(int craneId) => new() { XDisplay = 100, PressureLimit = false };
    }
}
