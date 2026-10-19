using AutomatedLine.Application.Configuration;
using AutomatedLine.Application.Generation;
using AutomatedLine.Application.Line;
using AutomatedLine.Application.Reservations;
using AutomatedLine.Application.Routing;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Domain.Routing;
using AutomatedLine.Domain.Workpieces;
using AutomatedLine.Tests.Fakes;

namespace AutomatedLine.Tests;

/// <summary>验证工艺首次启动冻结、ERP整批一致、模板持久化失败及真实生成器不绕过必做工序。</summary>
public sealed class ProcessPlanTests
{
    /// <summary>手动创建不锁定，首次启动才用最新配置；旧Waiting和暂停恢复均保留原快照。</summary>
    [Fact]
    public void FirstStart_UsesLatestTemplate_AndResumeKeepsOriginal()
    {
        var h = new Harness();
        var old = h.Manager.Add(Info(1));
        Assert.Null(old.Runtime.ProcessPlan);
        h.Plans.SetEnabled(ProcessType.Boring, false);
        Assert.True(h.Manager.Start(old.Id));
        var original = old.Runtime.ProcessPlan!;
        Assert.Equal(ProcessType.Marking, original.GetNext(ProcessType.Loading)!.Process);

        h.Plans.SetEnabled(ProcessType.Boring, true);
        h.Plans.SetEnabled(ProcessType.Marking, false);
        Assert.Same(original, old.Runtime.ProcessPlan);
        Assert.True(h.Manager.Pause(old.Id));
        Assert.True(h.Manager.Start(old.Id));
        Assert.Same(original, old.Runtime.ProcessPlan);
        var fresh = h.Manager.Add(Info(2));
        h.Manager.Start(fresh.Id);
        Assert.Equal(ProcessType.Boring, fresh.Runtime.ProcessPlan!.GetNext(ProcessType.Loading)!.Process);
        Assert.Equal(ProcessType.Unloading, fresh.Runtime.ProcessPlan.GetNext(ProcessType.Boring)!.Process);
        Assert.NotEqual(original.Version, fresh.Runtime.ProcessPlan.Version);
    }

    /// <summary>当前在设备上的工件保留完整原路线，取消全局勾选不会覆盖CurrentProcess。</summary>
    [Fact]
    public void OnDevice_AndFaultRollback_DoNotChangePlan()
    {
        var h = new Harness();
        var task = h.Manager.TryAddImported(Info(1))!;
        var original = task.Runtime.ProcessPlan!;
        task.Runtime.CurrentProcess = ProcessType.Boring;
        task.Runtime.CurrentLocation = WorkpieceLocation.AtDevice(h.Boring.Id);
        task.Runtime.TaskState = WorkpieceState.Running;
        h.Plans.SetEnabled(ProcessType.Marking, false);
        task.Runtime.TaskState = WorkpieceState.Waiting;
        Assert.Same(original, task.Runtime.ProcessPlan);
        Assert.Equal(ProcessType.Boring, task.Runtime.CurrentProcess);
        Assert.Equal(ProcessType.Marking, original.GetNext(task.Runtime.CurrentProcess)!.Process);
    }

    /// <summary>ERP整批只捕获一次；即使捕获后立即修改模板，同批仍用同一份，下一批才采用新路线。</summary>
    [Fact]
    public void ErpBatch_CapturesOnce_AndNextBatchUsesNewTemplate()
    {
        var h = new Harness();
        var captureCount = 0;
        var gateway = new ScriptedPlans(h.Plans, () =>
        {
            var plan = h.Plans.Capture();
            if (++captureCount == 1) h.Plans.SetEnabled(ProcessType.Boring, false);
            return plan;
        });
        var manager = new RuntimeTaskManager(h.Logger, gateway);
        var batch = manager.TryAddImportedBatch(new[] { Info(1), Info(2), Info(1), Info(3) });
        Assert.Equal(1, captureCount);
        Assert.Null(batch[2]);
        Assert.All(batch.Where(task => task is not null), task =>
        {
            Assert.Same(batch[0]!.Runtime.ProcessPlan, task!.Runtime.ProcessPlan);
            Assert.Equal(ProcessType.Boring, task.Runtime.ProcessPlan!.GetNext(ProcessType.Loading)!.Process);
            Assert.Equal(WorkpieceState.Waiting, task.Runtime.TaskState);
        });
        var next = manager.TryAddImported(Info(4))!;
        Assert.Equal(2, captureCount);
        Assert.Equal(ProcessType.Marking, next.Runtime.ProcessPlan!.GetNext(ProcessType.Loading)!.Process);
    }

    /// <summary>保存失败不发布新模板，原工艺仍可用于首次启动。</summary>
    [Fact]
    public void FailedSave_DoesNotChangeCurrentTemplate()
    {
        var h = new Harness();
        var before = h.Plans.Preview();
        h.Config.ProcessPlanSaveException = new IOException("模拟保存失败");
        Assert.Throws<IOException>(() => h.Plans.SetEnabled(ProcessType.Boring, false));
        Assert.Equal(before.Version, h.Plans.Capture().Version);
        Assert.False(h.Config.Options.ProcessPlan.EnabledProcesses.ContainsKey(ProcessType.Boring));
    }

    /// <summary>缺少固定上下料时启动失败，不能部分入队或消耗排位。</summary>
    [Fact]
    public void MissingUnload_RejectsManualAndBatchStartBeforePublishing()
    {
        var h = new Harness();
        h.Unload.IsEnabled = false;
        var manual = h.Manager.Add(Info(1));
        Assert.Throws<InvalidOperationException>(() => h.Manager.Start(manual.Id));
        Assert.Equal(WorkpieceState.Created, manual.Runtime.TaskState);
        Assert.Equal(0, manual.Runtime.QueueOrder);
        Assert.Null(manual.Runtime.ProcessPlan);
        Assert.Throws<InvalidOperationException>(() => h.Manager.TryAddImportedBatch(new[] { Info(2), Info(3) }));
        Assert.Single(h.Manager.Snapshot());
        h.Unload.IsEnabled = true;
        h.Manager.Start(manual.Id);
        Assert.Equal(1, manual.Runtime.QueueOrder);
    }

    /// <summary>上下料不能取消，无设备及未实现工序不能被启用。</summary>
    /// <param name="process">不允许调整或启用的工序。</param>
    [Theory]
    [InlineData(ProcessType.Loading)]
    [InlineData(ProcessType.Unloading)]
    [InlineData(ProcessType.Balancing)]
    [InlineData(ProcessType.Checking)]
    [InlineData(ProcessType.Grinding)]
    public void InvalidSelection_IsRejected(ProcessType process)
        => Assert.Throws<InvalidOperationException>(() => new Harness().Plans.SetEnabled(process, true));

    /// <summary>快照复制输入集合，二次绑定不能改变旧路线，未知当前工序也不能被误判完成。</summary>
    [Fact]
    public void Snapshot_IsIndependent_AndCannotBeOverwritten()
    {
        var nodes = ProcessRoute.CreateDefault().Nodes.Where(node => node.Process is ProcessType.Loading or ProcessType.Boring or ProcessType.Unloading).ToList();
        var plan = new ProcessPlanSnapshot(nodes);
        nodes.Clear();
        var runtime = new WorkpieceRuntime();
        runtime.LockProcessPlan(plan);
        runtime.LockProcessPlan(new ProcessPlanSnapshot(ProcessRoute.CreateDefault().Nodes));
        Assert.Same(plan, runtime.ProcessPlan);
        Assert.Equal(3, plan.Nodes.Count);
        Assert.Throws<InvalidOperationException>(() => plan.GetNext(ProcessType.Marking));
        Assert.Null(plan.GetNext(ProcessType.Unloading));
    }

    /// <summary>设备停用、断线或被移除后，已冻结的打号工序仍必须等待，不能跳到下料或完成。</summary>
    /// <param name="failure">设备不可用原因。</param>
    /// <returns>异步生成器验证任务。</returns>
    [Theory]
    [InlineData("disabled")]
    [InlineData("offline")]
    [InlineData("removed")]
    public async Task Generator_DoesNotBypassRequiredMarker(string failure)
    {
        var h = new Harness();
        var task = h.Manager.TryAddImported(Info(1))!;
        task.Runtime.CurrentProcess = ProcessType.Boring;
        task.Runtime.CurrentLocation = WorkpieceLocation.AtDevice(h.Boring.Id);
        h.Boring.CurrentWorkpiece = task;
        h.Plans.SetEnabled(ProcessType.Marking, false);
        if (failure == "disabled") h.Marker.IsEnabled = false;
        if (failure == "offline") h.Marker.Signals = null;
        if (failure == "removed")
        {
            h.Devices.Clear();
            h.Devices.Register(h.Load);
            h.Devices.Register(h.Boring);
            h.Devices.Register(h.Unload);
        }
        var candidates = await GenerateAsync(h);
        Assert.Empty(candidates);
        Assert.Equal(WorkpieceState.Waiting, task.Runtime.TaskState);
        Assert.Equal(ProcessType.Marking, task.Runtime.ProcessPlan!.GetNext(ProcessType.Boring)!.Process);
        Assert.Contains(h.Logger.Messages, message => message.Contains("MarkerMachine") && message.Contains("无可用目标"));
    }

    /// <summary>工序取消只影响新工件；真实生成器为新工件直送打号，原工件仍先去双头镗。</summary>
    /// <returns>异步候选验证任务。</returns>
    [Fact]
    public async Task Generator_UsesWorkpiecePlanRatherThanCurrentTemplate()
    {
        var h = new Harness();
        var old = h.Manager.TryAddImported(Info(1))!;
        h.Plans.SetEnabled(ProcessType.Boring, false);
        var fresh = h.Manager.TryAddImported(Info(2))!;
        var before = await GenerateAsync(h);
        Assert.Equal(h.Boring.Id, Assert.Single(before).Target.Id);
        h.Manager.Pause(old.Id);
        var after = await GenerateAsync(h);
        Assert.Equal(h.Marker.Id, Assert.Single(after).Target.Id);
        Assert.Equal(fresh.Id, after[0].Workpiece.Id);
    }

    /// <summary>历史异常数据缺少快照时保留Waiting，不能用新模板重写，也不能当已完成。</summary>
    /// <returns>异步异常候选验证任务。</returns>
    [Fact]
    public async Task Generator_MissingOriginalPlan_DoesNotRewriteOrComplete()
    {
        var h = new Harness();
        var legacy = h.Manager.Add(Info(1));
        legacy.Runtime.TaskState = WorkpieceState.Waiting;
        Assert.Empty(await GenerateAsync(h));
        Assert.Null(legacy.Runtime.ProcessPlan);
        Assert.Equal(WorkpieceState.Waiting, legacy.Runtime.TaskState);
        Assert.Contains(h.Logger.Messages, message => message.Contains("缺少原工艺快照"));
    }

    private static WorkpieceInfo Info(int serial) => new() { PlateNo = "P", SerialNo = serial, Diameter = 100, Length = 700 };

    // 运行真实宿主的一轮候选重建，不访问PLC，不依赖反射调用私有方法。
    private static async Task<IReadOnlyList<AutomatedLine.Domain.Tasks.CandidateTask>> GenerateAsync(Harness h)
    {
        var pool = new CandidateTaskPool();
        var rebuilt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pool.Changed += (_, _) => rebuilt.TrySetResult();
        var line = new LineControlService(h.Logger);
        line.Start();
        var generator = new TransportTaskGenerator(h.Manager, h.Devices, pool, new ResourceReservationManager(h.Logger), line, h.Config, h.Logger);
        await generator.StartAsync(default);
        try { await rebuilt.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { await generator.StopAsync(default); }
        return pool.Snapshot();
    }

    private sealed class Harness
    {
        public FakeConfigService Config { get; } = new();
        public TestLogger Logger { get; } = new();
        public DeviceRegistry Devices { get; } = new();
        public LoadRack Load { get; } = new(1, "上料", 0);
        public BoringMachine Boring { get; } = new(2, "双头镗", 1000);
        public MarkerMachine Marker { get; } = new(3, "打号", 2000);
        public UnloadRack Unload { get; } = new(4, "下料", 3000);
        public ProcessPlanService Plans { get; }
        public RuntimeTaskManager Manager { get; }

        public Harness()
        {
            foreach (var device in new DeviceBase[] { Load, Boring, Marker, Unload })
            {
                device.MaxDiameter = 1000;
                device.MaxLength = 10000;
                device.Signals = TestSignals.Of(("HasMaterial", device == Load), ("RequestData", true),
                    ("RequestLoad", true), ("RequestUnload", true), ("Busy", false));
                Devices.Register(device);
            }
            Plans = new ProcessPlanService(Config, Devices, Logger);
            Manager = new RuntimeTaskManager(Logger, Plans);
        }
    }

    private sealed class ScriptedPlans(IProcessPlanService inner, Func<ProcessPlanSnapshot> capture) : IProcessPlanService
    {
        public ProcessPlanOptions GetSettings() => inner.GetSettings();
        public ProcessPlanSnapshot Preview() => inner.Preview();
        public ProcessPlanSnapshot Capture() => capture();
        public ProcessPlanSnapshot SetEnabled(ProcessType process, bool enabled) => inner.SetEnabled(process, enabled);
    }
}
