using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Line;
using AutomatedLine.Application.Reservations;
using AutomatedLine.Application.RightOfWay;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Cranes;
using AutomatedLine.Domain.Diagnostics;
using AutomatedLine.Domain.Tasks;
using AutomatedLine.Domain.Workpieces;
using Microsoft.Extensions.Hosting;

namespace AutomatedLine.Application.Scheduling;

/// <summary>
/// 集中式调度器（架构文档 §8.4）。
/// 全系统只有一个调度器，统一决定"哪个任务 → 哪台天车"，避免两台天车抢同一任务。
/// 派发流程：选天车 → 原子获取路权 → 预约源/目标设备 → 占用天车执行锁 → 置 CurrentTask，Worker 推进状态机。
/// 路权统一在**派发时**获取；失败则本周期跳过（此时尚未占用天车，无需回滚）。
/// </summary>
public sealed class CraneScheduler : IHostedService
{
    /// <summary>候选任务池。</summary>
    private readonly CandidateTaskPool _candidatePool;

    /// <summary>天车注册表。</summary>
    private readonly CraneRegistry _craneRegistry;

    /// <summary>驱动注册表（按需读天车状态）。</summary>
    private readonly DriverRegistry _driverRegistry;

    /// <summary>路权管理器。</summary>
    private readonly RightOfWayManager _rightOfWayManager;

    /// <summary>设备预约管理器。</summary>
    private readonly ResourceReservationManager _reservationManager;

    /// <summary>线路控制（只有运行中才派发）。</summary>
    private readonly LineControlService _lineControl;

    /// <summary>配置服务。</summary>
    private readonly IConfigService _configService;

    /// <summary>日志。</summary>
    private readonly IAppLogger _logger;

    /// <summary>取消源。</summary>
    private CancellationTokenSource? _cts;

    /// <summary>循环任务。</summary>
    private Task? _loop;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="candidatePool">候选任务池</param>
    /// <param name="craneRegistry">天车注册表</param>
    /// <param name="driverRegistry">驱动注册表（按需读天车状态）</param>
    /// <param name="rightOfWayManager">路权管理器</param>
    /// <param name="reservationManager">预约管理器</param>
    /// <param name="lineControl">线路控制</param>
    /// <param name="configService">配置服务</param>
    /// <param name="logger">日志</param>
    public CraneScheduler(
        CandidateTaskPool candidatePool,
        CraneRegistry craneRegistry,
        DriverRegistry driverRegistry,
        RightOfWayManager rightOfWayManager,
        ResourceReservationManager reservationManager,
        LineControlService lineControl,
        IConfigService configService,
        IAppLogger logger)
    {
        _candidatePool = candidatePool;
        _craneRegistry = craneRegistry;
        _driverRegistry = driverRegistry;
        _rightOfWayManager = rightOfWayManager;
        _reservationManager = reservationManager;
        _lineControl = lineControl;
        _configService = configService;
        _logger = logger;
    }

    /// <summary>启动调度循环。</summary>
    /// <param name="cancellationToken">宿主取消令牌</param>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        Console.WriteLine("[CraneScheduler] 调度循环已启动");
        return Task.CompletedTask;
    }

    /// <summary>停止调度循环。</summary>
    /// <param name="cancellationToken">宿主取消令牌</param>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null)
        {
            return;
        }

        _cts.Cancel();
        if (_loop is not null)
        {
            await Task.WhenAny(_loop, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
        }

        _cts.Dispose();
        _cts = null;
    }

    /// <summary>调度主循环。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var interval = Math.Max(50, _configService.GetAppOptions().Scheduling.ScheduleIntervalMs);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_lineControl.IsRunning)
                {
                    await DispatchOnceAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(LogCategory.Scheduling, "调度异常", ex);
            }

            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>执行一轮派发（按需读空闲天车状态，不依赖后台采集）。</summary>
    private async Task DispatchOnceAsync(CancellationToken cancellationToken)
    {
        var cranes = _craneRegistry.Snapshot();

        // 只对"启用且空闲"的天车按需读一次状态（连接/报警/位置），供本轮选车使用。
        var statuses = await ReadIdleCraneStatusesAsync(cranes, cancellationToken).ConfigureAwait(false);

        var candidates = _candidatePool.Snapshot().OrderByDescending(c => c.Priority).ToList();

        foreach (var candidate in candidates)
        {
            var task = candidate.Workpiece;

            // 只派发仍在等待的任务（可能已被其它周期派发）
            if (task.Runtime.TaskState != WorkpieceState.Waiting)
            {
                continue;
            }

            var crane = SelectCrane(cranes, statuses, candidate.Source.PositionX, candidate.Target.PositionX);
            if (crane is null)
            {
                continue;
            }

            var interval = candidate.GetRightOfWayInterval(crane.Width);

            // 1) 原子获取路权（判断即加锁；拿不到就本周期跳过）
            if (!_rightOfWayManager.TryAcquire(task.Id, interval))
            {
                continue;
            }

            // 2) 预约源/目标设备；失败则释放路权并跳过
            if (!_reservationManager.TryReserve(task.Id, candidate.Source.Id, candidate.Target.Id))
            {
                _rightOfWayManager.Release(task.Id);
                continue;
            }

            // 3) 占用天车执行锁并生成正式任务
            var transportTask = new TransportTask
            {
                WorkpieceRuntimeId = task.Id,
                SourceDeviceId = candidate.Source.Id,
                TargetDeviceId = candidate.Target.Id,
                SourceX = candidate.Source.PositionX,
                TargetX = candidate.Target.PositionX,
                TargetProcess = candidate.TargetProcess,
                Priority = candidate.Priority,
                State = TransportTaskState.Assigned,
                AssignedCraneId = crane.Id
            };

            crane.Runtime.CurrentTask = transportTask;
            crane.Runtime.State = CraneState.Reserved;
            crane.Runtime.Step = TransportStep.PreCheck;
            crane.Runtime.LockedInterval = interval;
            task.Runtime.TaskState = WorkpieceState.Assigned;

            _logger.Info(LogCategory.Scheduling,
                $"派发：任务 {Short(task.Id)} {candidate.Source.Name}→{candidate.Target.Name} 给 {crane.Name}，区间 {interval}");
        }
    }

    /// <summary>
    /// 选择最合适的天车：空闲、启用、覆盖源/目标 X、连接正常无报警；优先距离源最近。
    /// 判断基于本轮按需读到的状态（不依赖后台采集快照）。
    /// </summary>
    /// <param name="cranes">天车快照</param>
    /// <param name="statuses">天车号 → 本轮按需读到的状态</param>
    /// <param name="sourceX">源 X</param>
    /// <param name="targetX">目标 X</param>
    /// <returns>天车；无可用返回 null</returns>
    private static Crane? SelectCrane(IReadOnlyList<Crane> cranes, IReadOnlyDictionary<int, CraneStatusDto?> statuses, double sourceX, double targetX)
    {
        return cranes
            .Where(c => c.IsEnabled
                        && c.IsIdle
                        && c.Covers(sourceX)
                        && c.Covers(targetX)
                        && statuses.TryGetValue(c.Id, out var s) && s is not null && !s.HasAlarm)
            .OrderBy(c => Math.Abs(statuses[c.Id]!.XDisplay - sourceX))
            .FirstOrDefault();
    }

    /// <summary>
    /// 对"启用且空闲"的天车按需读取状态（连接/报警/位置），供本轮选车使用。
    /// </summary>
    /// <param name="cranes">天车快照</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>天车号 → 状态（读取失败为 null）</returns>
    private async Task<Dictionary<int, CraneStatusDto?>> ReadIdleCraneStatusesAsync(IReadOnlyList<Crane> cranes, CancellationToken cancellationToken)
    {
        var map = new Dictionary<int, CraneStatusDto?>();
        foreach (var crane in cranes)
        {
            if (!crane.IsEnabled || !crane.IsIdle)
            {
                continue;
            }

            var driver = _driverRegistry.GetCrane(crane.Id);
            if (driver is null)
            {
                map[crane.Id] = null;
                continue;
            }

            try
            {
                map[crane.Id] = await driver.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warn(LogCategory.Scheduling, $"读取天车[{crane.Id}] 状态失败：{ex.Message}");
                map[crane.Id] = null;
            }
        }

        return map;
    }

    /// <summary>任务标识短文本，用于日志。</summary>
    /// <param name="taskId">任务标识</param>
    /// <returns>前 8 位</returns>
    private static string Short(Guid taskId) => taskId.ToString("N")[..8];
}
