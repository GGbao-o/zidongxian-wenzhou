using System.Diagnostics;
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Configuration;
using AutomatedLine.Application.Line;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Cranes;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Domain.Tasks;
using AutomatedLine.Domain.Workpieces;
using AutomatedLine.Tests.Fakes;

namespace AutomatedLine.Tests;

/// <summary>验证完成保留期限、批量通知、引用并发安全、ERP再次接收及独立后台配置生效。</summary>
public sealed class CompletedTaskCleanupTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    /// <summary>完成时间按UTC保存且与最终状态一起可见，不会把中间工序当完成。</summary>
    [Fact]
    public void Completion_RecordsUtcBeforePublishingCompleted()
    {
        var runtime = new WorkpieceRuntime();
        Assert.Null(runtime.CompletedAtUtc);
        runtime.MarkCompleted(Now.ToOffset(TimeSpan.FromHours(8)));
        Assert.Equal(WorkpieceState.Completed, runtime.TaskState);
        Assert.Equal(Now, runtime.CompletedAtUtc);
        Assert.Equal(TimeSpan.Zero, runtime.CompletedAtUtc!.Value.Offset);
    }

    /// <summary>达到60秒才清理；其他状态即使有旧完成时间也保留，缺完成时间同样保留。</summary>
    [Fact]
    public void Batch_DeletesOnlyExpiredCompletedAndNotifiesOnceOutsideLocks()
    {
        var h = new Harness();
        var first = h.Add(1, -60);
        var second = h.Add(2, -120);
        var recent = h.Add(3, -59.999);
        var missingTime = h.Add(4);
        missingTime.Runtime.TaskState = WorkpieceState.Completed;
        foreach (var state in Enum.GetValues<WorkpieceState>().Where(state => state != WorkpieceState.Completed))
            h.Add(100 + (int)state, -120).Runtime.TaskState = state;
        h.Device.CurrentWorkpiece = first;
        h.Crane.CurrentWorkpiece = second;
        var notifications = 0;
        var clearedBeforeNotification = false;
        var queryCompletedDuringNotification = false;
        using var queried = new ManualResetEventSlim();
        Thread? queryThread = null;
        h.Tasks.Changed += (_, _) =>
        {
            notifications++;
            clearedBeforeNotification = h.Device.CurrentWorkpiece is null && h.Crane.CurrentWorkpiece is null;
            // 另一个线程能够立即查询，证明没有持任务表锁同步回调UI。
            queryThread = new Thread(() => { h.Tasks.Snapshot(); queried.Set(); }) { IsBackground = true };
            queryThread.Start();
            queryCompletedDuringNotification = queried.Wait(TimeSpan.FromSeconds(2));
        };
        var removed = h.Tasks.RemoveCompleted(Now.AddSeconds(-60), h.Devices, h.Cranes);
        Assert.True(queryThread!.Join(TimeSpan.FromSeconds(2)));
        Assert.True(queryCompletedDuringNotification, "通知必须在任务表锁外执行");
        Assert.True(clearedBeforeNotification);
        Assert.Equal(new[] { first.Id, second.Id }, removed.Select(task => task.Id));
        Assert.Same(recent, h.Tasks.Get(recent.Id));
        Assert.Same(missingTime, h.Tasks.Get(missingTime.Id));
        Assert.Equal(Enum.GetValues<WorkpieceState>().Length + 1, h.Tasks.Snapshot().Count);
        Assert.Equal(1, notifications);
        Assert.Empty(h.Tasks.RemoveCompleted(Now.AddSeconds(-60), h.Devices, h.Cranes));
        Assert.Equal(1, notifications);
        Assert.Equal(2, h.Logger.Messages.Count(message => message.Contains("自动删除已完成任务：")));
        Assert.Contains(h.Logger.Messages, message => message.Contains("已清设备引用=1"));
    }

    /// <summary>同版号同序号的新工件有新Guid；自动清理旧记录不能清掉它或改变天车搬运任务。</summary>
    [Fact]
    public void Cleanup_PreservesReplacementWithSameBusinessIdentity()
    {
        var h = new Harness();
        var old = h.Add(1, -60);
        var next = new WorkpieceTask(new WorkpieceInfo { PlateNo = old.Info.PlateNo, SerialNo = old.Info.SerialNo });
        h.Device.CurrentWorkpiece = next;
        h.Crane.CurrentWorkpiece = next;
        var move = new TransportTask { WorkpieceRuntimeId = next.Id };
        h.Crane.Runtime.CurrentTask = move;
        h.Tasks.RemoveCompleted(Now, h.Devices, h.Cranes);
        Assert.Same(next, h.Device.CurrentWorkpiece);
        Assert.Same(next, h.Crane.CurrentWorkpiece);
        Assert.Same(move, h.Crane.Runtime.CurrentTask);
        Assert.Contains(h.Logger.Messages, message => message.Contains("已清设备引用=0、天车引用=0"));
    }

    /// <summary>读取旧对象后引用已被替换，原子清理必须失败；即使两个对象有相同Guid也不能覆盖新对象。</summary>
    [Fact]
    public void AtomicClear_DoesNotOverwriteObjectInstalledAfterObservation()
    {
        var h = new Harness();
        var old = h.Add(1, -60);
        var replacement = new WorkpieceTask(new WorkpieceInfo { RuntimeId = old.Id });
        h.Device.CurrentWorkpiece = replacement;
        h.Crane.CurrentWorkpiece = replacement;
        Assert.False(h.Device.TryClearWorkpiece(old));
        Assert.False(h.Crane.TryClearWorkpiece(old));
        Assert.Same(replacement, h.Device.CurrentWorkpiece);
        Assert.Same(replacement, h.Crane.CurrentWorkpiece);
    }

    /// <summary>删除前ERP仍拒绝重复；删除后创建新Guid并排到队尾，不复活旧任务。</summary>
    [Fact]
    public void Erp_ReacceptsIdentityOnlyAfterCompletedRecordIsRemoved()
    {
        var h = new Harness();
        var info = new WorkpieceInfo { PlateNo = "REPEAT", SerialNo = 0 };
        var old = h.Tasks.TryAddImported(info)!;
        old.Runtime.MarkCompleted(Now.AddSeconds(-60));
        var previousOrder = old.Runtime.QueueOrder;
        Assert.Null(h.Tasks.TryAddImported(new WorkpieceInfo { PlateNo = "REPEAT", SerialNo = 0 }));
        h.Tasks.RemoveCompleted(Now.AddSeconds(-60), h.Devices, h.Cranes);
        var next = h.Tasks.TryAddImported(new WorkpieceInfo { PlateNo = "REPEAT", SerialNo = 0 })!;
        Assert.NotEqual(old.Id, next.Id);
        Assert.Equal(WorkpieceState.Waiting, next.Runtime.TaskState);
        Assert.True(next.Runtime.QueueOrder > previousOrder);
        Assert.Null(next.Runtime.CompletedAtUtc);
        Assert.NotSame(old.Runtime.ProcessPlan, next.Runtime.ProcessPlan);
    }

    /// <summary>页面通知异常不回滚已经完成的删除，也不重复发送通知或重删。</summary>
    [Fact]
    public void NotificationFailure_DoesNotUndoCommittedDeletion()
    {
        var h = new Harness();
        var old = h.Add(1, -60);
        h.Tasks.Changed += (_, _) => throw new InvalidOperationException("模拟页面通知失败");
        Assert.Single(h.Tasks.RemoveCompleted(Now, h.Devices, h.Cranes));
        Assert.Null(h.Tasks.Get(old.Id));
        Assert.Empty(h.Tasks.RemoveCompleted(Now, h.Devices, h.Cranes));
        Assert.Contains(h.Logger.Messages, message => message.Contains("页面通知失败"));
    }

    /// <summary>暂停线路仍清理；每轮读取开关、保留和间隔配置，宿主停止可中断等待。</summary>
    /// <returns>异步后台循环验证任务。</returns>
    [Fact]
    public async Task Worker_ReloadsSettingsAndRunsWhileLineIsPausedWithoutAnyPage()
    {
        var h = new Harness();
        var old = h.Add(1, -60);
        var config = new Config();
        config.Options.CompletedTaskCleanup.Enabled = false;
        var clock = new Clock();
        var line = new LineControlService(h.Logger);
        line.Start();
        line.Pause();
        using var service = new CompletedTaskCleanupService(h.Tasks, h.Devices, h.Cranes, config, h.Logger, clock);
        await service.StartAsync(default);
        await clock.WaitForDelays(1);
        Assert.Same(old, h.Tasks.Get(old.Id));
        config.Options.CompletedTaskCleanup.Enabled = true;
        config.Options.CompletedTaskCleanup.CheckIntervalSeconds = 2;
        clock.Advance(10);
        await clock.WaitForDelays(2);
        Assert.Null(h.Tasks.Get(old.Id));
        Assert.False(line.IsRunning);
        Assert.Equal(TimeSpan.FromSeconds(2), clock.LastDueTime);
        await service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains(h.Logger.Messages, message => message.Contains("自动清理批次完成：删除1件"));
        Assert.Contains(h.Logger.Messages, message => message.Contains("自动清理服务停止"));
    }

    /// <summary>非法保留/间隔或配置读取失败时保留记录，下一轮恢复后继续工作，不结束宿主。</summary>
    /// <param name="failure">非法参数或配置异常类型。</param>
    /// <returns>异步后台恢复验证任务。</returns>
    [Theory]
    [InlineData("retention")]
    [InlineData("interval")]
    [InlineData("huge")]
    [InlineData("read")]
    public async Task Worker_RecoversFromInvalidConfigurationOrReadFailure(string failure)
    {
        var h = new Harness();
        var old = h.Add(1, -60);
        var config = new Config { FailRead = failure == "read" };
        if (failure == "retention") config.Options.CompletedTaskCleanup.RetentionSeconds = -1;
        if (failure == "interval") config.Options.CompletedTaskCleanup.CheckIntervalSeconds = 0;
        if (failure == "huge") config.Options.CompletedTaskCleanup.CheckIntervalSeconds = int.MaxValue;
        var clock = new Clock();
        using var service = new CompletedTaskCleanupService(h.Tasks, h.Devices, h.Cranes, config, h.Logger, clock);
        await service.StartAsync(default);
        await clock.WaitForDelays(1);
        Assert.Same(old, h.Tasks.Get(old.Id));
        config.FailRead = false;
        config.Options.CompletedTaskCleanup.RetentionSeconds = 0;
        config.Options.CompletedTaskCleanup.CheckIntervalSeconds = 1;
        clock.Advance(10);
        await clock.WaitForDelays(2);
        Assert.Null(h.Tasks.Get(old.Id));
        await service.StopAsync(default);
        Assert.Contains(h.Logger.Messages, message => message.StartsWith("W:"));
    }

    private sealed class Harness
    {
        public TestLogger Logger { get; } = new();
        public RuntimeTaskManager Tasks { get; }
        public DeviceRegistry Devices { get; } = new();
        public CraneRegistry Cranes { get; } = new();
        public UnloadRack Device { get; } = new(1, "下料架", 5000);
        public Crane Crane { get; } = new(1, 1, "天车1");
        public Harness()
        {
            Tasks = new RuntimeTaskManager(Logger, TestProcessPlans.Create());
            Devices.Register(Device);
            Cranes.Register(Crane);
        }
        public WorkpieceTask Add(int serial, double? secondsAgo = null)
        {
            var task = Tasks.Add(new WorkpieceInfo { PlateNo = "P", SerialNo = serial });
            if (secondsAgo.HasValue) task.Runtime.MarkCompleted(Now.AddSeconds(secondsAgo.Value));
            return task;
        }
    }

    private sealed class Config : IConfigService
    {
        public AppOptions Options { get; } = new();
        public bool FailRead { get; set; }
        public AppOptions GetAppOptions() => FailRead ? throw new InvalidOperationException("模拟配置读取失败") : Options;
        public string GetConnectionString() => string.Empty;
        public void SaveAppOptions(AppOptions options) => throw new NotSupportedException();
        public void SaveProcessPlanOptions(ProcessPlanOptions options) => throw new NotSupportedException();
    }

    // 手动驱动的是生产Task.Delay的计时器，避免用真实的10秒等待掩盖后台循环错误。
    private sealed class Clock : TimeProvider
    {
        private long _ticks = Now.UtcDateTime.Ticks;
        private int _delays;
        private DelayTimer? _timer;
        public TimeSpan LastDueTime { get; private set; }
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            LastDueTime = dueTime;
            var timer = new DelayTimer(callback, state);
            Volatile.Write(ref _timer, timer);
            Interlocked.Increment(ref _delays);
            return timer;
        }
        public void Advance(int seconds)
        {
            Interlocked.Add(ref _ticks, TimeSpan.FromSeconds(seconds).Ticks);
            Volatile.Read(ref _timer)!.Fire();
        }
        public async Task WaitForDelays(int count)
        {
            var elapsed = Stopwatch.StartNew();
            while (Volatile.Read(ref _delays) < count && elapsed.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(5);
            Assert.True(Volatile.Read(ref _delays) >= count, "后台循环未进入预期等待");
        }
        private sealed class DelayTimer(TimerCallback callback, object? state) : ITimer
        {
            private int _disposed;
            public void Fire() { if (Interlocked.Exchange(ref _disposed, 1) == 0) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;
            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
