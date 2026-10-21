using System.Collections.Concurrent;
using System.Reflection;
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Alarms;
using AutomatedLine.Application.Collection;
using AutomatedLine.Application.Line;
using AutomatedLine.Application.Reservations;
using AutomatedLine.Application.RightOfWay;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Application.Scheduling;
using AutomatedLine.Domain.Cranes;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Domain.Diagnostics;
using AutomatedLine.Domain.Routing;
using AutomatedLine.Domain.Tasks;
using AutomatedLine.Domain.Workpieces;
using AutomatedLine.Tests.Fakes;

namespace AutomatedLine.Tests;

/// <summary>共享天车快照的时效、跨车隔离、配置身份和实际业务消费回归，不连接现场 PLC。</summary>
public sealed class CraneStateCollectionTests
{
    /// <summary>失败必须替换成功快照，序号递增；单个未知点位仍保留其未知语义。</summary>
    [Fact]
    public void Store_FailureReplacesSuccessAndKeepsUnknownPoints()
    {
        var h = new Harness();
        Assert.Null(h.Store.GetFreshStatus(1));
        var status = new CraneStatusDto { XDisplay = 42, HasPlate = null };
        h.Publish(status);
        var first = h.Store.GetSnapshot(1)!;
        Assert.Same(status, h.Store.GetFreshStatus(1));
        Assert.Null(h.Store.GetFreshStatus(1)!.HasPlate);
        h.Publish(null, "连接超时");
        Assert.Null(h.Store.GetFreshStatus(1));
        Assert.Null(h.Store.GetSnapshot(1)!.Status);
        Assert.Equal("连接超时", h.Store.GetSnapshot(1)!.FailureReason);
        Assert.True(h.Store.GetSnapshot(1)!.Sequence > first.Sequence);
        Assert.Same(status, first.Status);
    }

    /// <summary>快照从读取开始计龄，慢读取不能在结束时重置为新鲜；校时不影响单调期限。</summary>
    [Fact]
    public void Store_AgeStartsBeforeReadAndIgnoresWallClockChanges()
    {
        var clock = new Clock();
        var h = new Harness(clock);
        var timestamp = clock.GetTimestamp();
        var startedAt = clock.GetUtcNow();
        clock.Advance(2001);
        h.Store.Publish(h.Driver, new CraneStatusDto(), timestamp, startedAt);
        Assert.Null(h.Store.GetFreshStatus(1));
        h.Publish(new CraneStatusDto());
        clock.ShiftWallClock(TimeSpan.FromDays(2));
        Assert.NotNull(h.Store.GetFreshStatus(1));
        clock.Advance(2001);
        clock.ShiftWallClock(TimeSpan.FromDays(-4));
        Assert.Null(h.Store.GetFreshStatus(1));
    }

    /// <summary>重载后的新驱动不能使用旧快照，旧读取晚返回也不能覆盖新结果。</summary>
    [Fact]
    public void Store_DriverReplacementRejectsOldLateResults()
    {
        var h = new Harness();
        h.Publish(new CraneStatusDto { XDisplay = 1 });
        var newer = new FakeCraneDriver { CraneId = 1 };
        h.Drivers.RegisterCrane(newer);
        Assert.Null(h.Store.GetSnapshot(1));
        h.Store.Publish(newer, new CraneStatusDto { XDisplay = 2 }, h.Time.GetTimestamp(), h.Time.GetUtcNow());
        h.Publish(new CraneStatusDto { XDisplay = 3 });
        Assert.Equal(2, h.Store.GetFreshStatus(1)!.XDisplay);
        h.Crane.IsEnabled = false;
        Assert.Null(h.Store.GetFreshStatus(1));
    }

    /// <summary>一台车等待通信时另一台持续发布，慢车不重入；宿主停止会结束待处理读取。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task Collector_SlowCraneDoesNotBlockOtherCraneOrOverlap()
    {
        var h = new Harness();
        var waiting = new TaskCompletionSource<CraneStatusDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Driver.OnReadStatus = ct => waiting.Task.WaitAsync(ct);
        var fast = new FakeCraneDriver { CraneId = 2, Status = new CraneStatusDto { XDisplay = 777 } };
        h.AddCrane(fast);
        using var collector = h.Collector();
        await collector.StartAsync(CancellationToken.None);
        try
        {
            await UntilAsync(() => fast.StatusReadCount >= 3);
            Assert.Equal(1, h.Driver.StatusReadCount);
            Assert.Equal(777, h.Store.GetFreshStatus(2)!.XDisplay);
            Assert.Null(h.Store.GetSnapshot(1));
        }
        finally { await collector.StopAsync(CancellationToken.None); }
        Assert.Null(h.Store.GetSnapshot(1));
        var count = fast.StatusReadCount;
        await Task.Delay(100);
        Assert.Equal(count, fast.StatusReadCount);
    }

    /// <summary>单次读取异常发布不可用结果，循环继续并记录恢复，不终止宿主采集。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task Collector_FailureIsPublishedThenRecovers()
    {
        var h = new Harness();
        var recovery = new TaskCompletionSource<CraneStatusDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Driver.OnReadStatus = ct => h.Driver.StatusReadCount == 1
            ? Task.FromException<CraneStatusDto?>(new TimeoutException("测试通信超时")) : recovery.Task.WaitAsync(ct);
        using var collector = h.Collector();
        await collector.StartAsync(CancellationToken.None);
        try
        {
            await UntilAsync(() => h.Driver.StatusReadCount >= 2);
            Assert.Null(h.Store.GetFreshStatus(1));
            Assert.Equal("测试通信超时", h.Store.GetSnapshot(1)!.FailureReason);
            recovery.SetResult(new CraneStatusDto { XDisplay = 9 });
            await UntilAsync(() => h.Store.GetFreshStatus(1)?.XDisplay == 9);
        }
        finally { await collector.StopAsync(CancellationToken.None); }
        Assert.Contains(h.Logger.Messages, m => m.Contains("采集失败"));
        Assert.Contains(h.Logger.Messages, m => m.Contains("恢复成功"));
        Assert.Contains(h.Logger.Messages, m => m.Contains("采集已停止"));
    }

    /// <summary>采集运行期间新装载的车得到独立循环，驱动替换后原车循环使用新驱动。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task Collector_FollowsAddedAndReplacedDrivers()
    {
        var h = new Harness();
        using var collector = h.Collector();
        await collector.StartAsync(CancellationToken.None);
        try
        {
            await UntilAsync(() => h.Store.GetFreshStatus(1) is not null);
            var added = new FakeCraneDriver { CraneId = 2 };
            h.AddCrane(added);
            var replaced = new FakeCraneDriver { CraneId = 1, Status = new CraneStatusDto { XDisplay = 55 } };
            h.Drivers.RegisterCrane(replaced);
            await UntilAsync(() => h.Store.GetFreshStatus(2) is not null && h.Store.GetFreshStatus(1)?.XDisplay == 55);
            Assert.True(replaced.StatusReadCount > 0);
        }
        finally { await collector.StopAsync(CancellationToken.None); }
    }

    /// <summary>调度只用有效缓存；不可用、报警、下压触发或未知均不派发，也不额外读取驱动。</summary>
    /// <param name="state">缓存场景：有效、未采集、过期、失败、报警、下压触发或未知。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData("fresh")]
    [InlineData("missing")]
    [InlineData("expired")]
    [InlineData("failed")]
    [InlineData("alarm")]
    [InlineData("pressure")]
    [InlineData("pressure-unknown")]
    public async Task Scheduler_OnlyFreshUsableCacheDispatches(string state)
    {
        var clock = new Clock();
        var h = new Harness(clock);
        var workpiece = new WorkpieceTask(new WorkpieceInfo());
        var pool = new CandidateTaskPool();
        pool.Replace(new[] { new CandidateTask(workpiece, new LoadRack(1, "源", 100), new UnloadRack(2, "目标", 200), 0, ProcessType.Unloading) });
        workpiece.Runtime.TaskState = WorkpieceState.Waiting;
        if (state != "missing") h.Publish(state == "failed" ? null : new CraneStatusDto
        {
            PlcAlarm = state == "alarm" ? 1 : 0,
            PressureLimit = state == "pressure-unknown" ? null : state == "pressure"
        });
        if (state == "expired") clock.Advance(2001);
        var channels = new CraneTaskChannels();
        var scheduler = new CraneScheduler(pool, channels, h.Cranes, h.Store, new RightOfWayManager(h.Logger),
            new ResourceReservationManager(h.Logger), new LineControlService(h.Logger), h.Config, h.Logger);
        await (Task)typeof(CraneScheduler).GetMethod("DispatchOnceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scheduler, new object[] { false, CancellationToken.None })!;
        Assert.Equal(state == "fresh", channels.For(1).Reader.TryRead(out _));
        Assert.Equal(state == "fresh" ? WorkpieceState.Assigned : WorkpieceState.Waiting, workpiece.Runtime.TaskState);
        Assert.Equal(0, h.Driver.StatusReadCount);
        if (state.StartsWith("pressure")) Assert.Contains(h.Logger.Messages, m => m.Contains(state == "pressure" ? "下压信号已触发" : "下压信号未知"));
    }

    /// <summary>最近车辆下压触发或未知时改选远处可执行车，候选距离计算与派发筛选保持一致。</summary>
    /// <param name="pressure">最近车辆的下压信号，true或未知均应排除。</param>
    /// <returns>异步调度验证任务。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public async Task Scheduler_PressureBlockedNearestCraneDoesNotPreventOtherCrane(bool? pressure)
    {
        var h = new Harness();
        var near = new CraneStatusDto { XDisplay = 100, PressureLimit = pressure };
        h.Publish(near);
        var other = new FakeCraneDriver { CraneId = 2 };
        h.AddCrane(other);
        h.Cranes.Get(2)!.StartX = 0;
        h.Cranes.Get(2)!.EndX = 1000;
        var far = new CraneStatusDto { XDisplay = 500, PressureLimit = false };
        h.Store.Publish(other, far, h.Time.GetTimestamp(), h.Time.GetUtcNow());
        var workpiece = new WorkpieceTask(new WorkpieceInfo());
        workpiece.Runtime.TaskState = WorkpieceState.Waiting;
        var candidate = new CandidateTask(workpiece, new LoadRack(1, "源", 100), new UnloadRack(2, "目标", 200), 0, ProcessType.Unloading);
        var pool = new CandidateTaskPool();
        pool.Replace(new[] { candidate });
        var channels = new CraneTaskChannels();
        var scheduler = new CraneScheduler(pool, channels, h.Cranes, h.Store, new RightOfWayManager(h.Logger),
            new ResourceReservationManager(h.Logger), new LineControlService(h.Logger), h.Config, h.Logger);
        var distance = (double)typeof(CraneScheduler).GetMethod("NearestCraneDistance", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { candidate, h.Cranes.Snapshot(), new Dictionary<int, CraneStatusDto?> { [1] = near, [2] = far } })!;
        Assert.Equal(400d, distance);
        await (Task)typeof(CraneScheduler).GetMethod("DispatchOnceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scheduler, new object[] { false, CancellationToken.None })!;
        Assert.False(channels.For(1).Reader.TryRead(out _));
        Assert.True(channels.For(2).Reader.TryRead(out _));
        Assert.Equal(WorkpieceState.Assigned, workpiece.Runtime.TaskState);
        Assert.Equal(0, h.Driver.StatusReadCount);
        Assert.Equal(0, other.StatusReadCount);
    }

    /// <summary>冷启动从有效缓存判断有板，仅沿用原提示，不自行读取 PLC。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task ColdStart_UsesKnownFreshPlateWithoutExtraReads()
    {
        var h = new Harness();
        h.Publish(new CraneStatusDto { HasPlate = true });
        var found = await h.Checker().CheckAsync();
        Assert.Single(found);
        Assert.Contains("现场有料", found[0]);
        Assert.Equal(0, h.Driver.StatusReadCount);
    }

    /// <summary>未知X11必须等到已知新快照；不能把未知或过期结果当作无板。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task ColdStart_WaitsForKnownPlateAndHonorsCancellation()
    {
        var h = new Harness();
        h.Config.Options.Collection.CommunicationTimeoutMs = 1000;
        h.Publish(new CraneStatusDto { HasPlate = null });
        var pending = h.Checker().CheckAsync();
        Assert.False(pending.IsCompleted);
        h.Publish(new CraneStatusDto { HasPlate = true });
        Assert.Single(await pending);
        h.Publish(null);
        using var cts = new CancellationTokenSource();
        var cancelled = h.Checker().CheckAsync(cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancelled);
    }

    /// <summary>两台不可用车同时等待一个期限，期限后记录未完成而非无板，保留仅提示策略。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task ColdStart_UnavailableCranesWaitInParallelAndLogIncomplete()
    {
        var h = new Harness();
        h.AddCrane(new FakeCraneDriver { CraneId = 2 });
        h.Config.Options.Collection.CommunicationTimeoutMs = 80;
        var tracking = new TrackingProvider(h.Store);
        var checker = new ColdStartChecker(new DeviceRegistry(), h.Cranes, tracking,
            new AlarmManager(new ActiveAlarmStore(), h.Logger), h.Config, h.Logger);
        var pending = checker.CheckAsync();
        Assert.Contains(1, tracking.ReadIds);
        Assert.Contains(2, tracking.ReadIds);
        var result = await pending;
        Assert.Empty(result);
        Assert.Equal(2, h.Logger.Messages.Count(m => m.Contains("核对未完成")));
        Assert.Equal(0, h.Driver.StatusReadCount);
    }

    /// <summary>过期有板结果不能触发本次核对结论，必须等待新快照或记录未完成。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task ColdStart_DoesNotUseExpiredPlateResult()
    {
        var clock = new Clock();
        var h = new Harness(clock);
        h.Config.Options.Collection.CommunicationTimeoutMs = 50;
        h.Publish(new CraneStatusDto { HasPlate = true });
        clock.Advance(2001);
        Assert.Empty(await h.Checker().CheckAsync());
        Assert.Contains(h.Logger.Messages, m => m.Contains("核对未完成"));
    }

    // 只等待实际状态，取消期限防止失败实现让测试永久挂起；不按固定延迟猜测后台是否已完成。
    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private sealed class Harness
    {
        public CraneRegistry Cranes { get; } = new();
        public DriverRegistry Drivers { get; } = new();
        public FakeConfigService Config { get; } = new();
        public ConcurrentLogger Logger { get; } = new();
        public FakeCraneDriver Driver { get; } = new();
        public Crane Crane { get; } = new(1, 1, "天车1") { IsEnabled = true, StartX = 0, EndX = 1000, Width = 10 };
        public TimeProvider Time { get; }
        public CraneStateStore Store { get; }
        public Harness(TimeProvider? time = null)
        {
            Time = time ?? TimeProvider.System;
            Config.Options.Collection.CraneIntervalMs = 50;
            Cranes.Register(Crane);
            Drivers.RegisterCrane(Driver);
            Store = new CraneStateStore(Cranes, Drivers, Config, Time);
        }
        public void Publish(CraneStatusDto? status, string? failure = null)
            => Store.Publish(Driver, status, Time.GetTimestamp(), Time.GetUtcNow(), failure);
        public void AddCrane(FakeCraneDriver driver)
        {
            Cranes.Register(new Crane(driver.CraneId, driver.CraneId, $"天车{driver.CraneId}") { IsEnabled = true });
            Drivers.RegisterCrane(driver);
        }
        public CraneStateCollector Collector() => new(Cranes, Drivers, Store, Config, Logger, Time);
        public ColdStartChecker Checker() => new(new DeviceRegistry(), Cranes, Store,
            new AlarmManager(new ActiveAlarmStore(), Logger), Config, Logger);
    }

    private sealed class Clock : TimeProvider
    {
        private long _milliseconds = 100_000;
        private TimeSpan _wallShift;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _milliseconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(_milliseconds) + _wallShift;
        public void Advance(long milliseconds) => _milliseconds += milliseconds;
        public void ShiftWallClock(TimeSpan amount) => _wallShift += amount;
    }

    private sealed class ConcurrentLogger : IAppLogger
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public void Info(LogCategory category, string message) => Messages.Enqueue("I:" + message);
        public void Warn(LogCategory category, string message) => Messages.Enqueue("W:" + message);
        public void Error(LogCategory category, string message, Exception? exception = null) => Messages.Enqueue("E:" + message);
        public void Sample(LogCategory category, string key, string message) => Messages.Enqueue("S:" + message);
    }

    private sealed class TrackingProvider(ICraneStateProvider inner) : ICraneStateProvider
    {
        public ConcurrentBag<int> ReadIds { get; } = new();
        public CraneStateSnapshot? GetSnapshot(int craneId) => inner.GetSnapshot(craneId);
        public CraneStatusDto? GetFreshStatus(int craneId)
        {
            ReadIds.Add(craneId);
            return inner.GetFreshStatus(craneId);
        }
    }
}
