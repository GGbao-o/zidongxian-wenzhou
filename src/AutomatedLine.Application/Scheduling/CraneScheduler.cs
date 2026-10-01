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

    /// <summary>
    /// 执行一轮派发（分配矩阵）。
    /// <para>
    /// **只判断、不加锁**：对每个候选列出可执行天车（范围覆盖 / 连接 / 无报警 / 空闲），
    /// 按"离源最近"排序后逐台尝试；用 <c>CanAcquire</c>（路权区间空闲）与 <c>IsReserved</c>（源/目标未被预约）
    /// 做**只读判断**，通过即派发；真正的路权/预约获取由**状态机执行任务时**完成（见 <c>TransportStep.AcquireResources</c>）。
    /// </para>
    /// <para>
    /// 竞态：判断通过后到状态机真正加锁之间有窗口，可能被别的车抢先；届时状态机 `TryAcquire` 失败会
    /// **回滚**（释放天车锁、任务回 Waiting），下一轮重新派发。
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    private async Task DispatchOnceAsync(CancellationToken cancellationToken)
    {
        var cranes = _craneRegistry.Snapshot();

        // 只对"启用且空闲"的天车按需读一次状态（连接/报警/位置），供本轮选车使用。天车不做后台采集。
        var statuses = await ReadIdleCraneStatusesAsync(cranes, cancellationToken).ConfigureAwait(false);

        // 候选排序：优先级降序 → 距离最近（最近可执行天车到源的距离；无可执行天车的排最后）。不做负载均衡。
        var candidates = _candidatePool.Snapshot()
            .OrderByDescending(c => c.Priority)
            .ThenBy(c => NearestCraneDistance(c, cranes, statuses))
            .ToList();

        // 本轮已派出的天车：一车一任务（硬约束）
        var usedCranes = new HashSet<int>();

        foreach (var candidate in candidates)
        {
            var task = candidate.Workpiece;

            // 只派发仍在"排队中"的任务（可能已被本轮/其它周期派发，或已暂停/完成）
            if (task.Runtime.TaskState != WorkpieceState.Waiting)
            {
                continue;
            }

            // 本候选的**可执行天车**：启用、空闲、本轮未用、范围覆盖源与目标、状态可读且无报警
            var eligible = cranes
                .Where(c => c.IsEnabled
                            && c.IsIdle
                            && !usedCranes.Contains(c.Id)
                            && c.Covers(candidate.Source.PositionX)
                            && c.Covers(candidate.Target.PositionX)
                            && statuses.TryGetValue(c.Id, out var s) && s is not null && !s.HasAlarm)
                .OrderBy(c => Math.Abs(statuses[c.Id]!.XDisplay - candidate.Source.PositionX))   // 离源最近
                .ToList();

            // 逐台车尝试（**换车**）：某台车判断不通过就试下一台，绝不死等
            foreach (var crane in eligible)
            {
                var interval = candidate.GetRightOfWayInterval(crane.Width);   // 每车宽度不同 → 区间不同

                // 【只判断·路权】区间是否空闲（不加锁）
                if (!_rightOfWayManager.CanAcquire(interval))
                {
                    continue;   // 区间被别的任务占用 → 试下一台车
                }

                // 【只判断·预约】源/目标是否已被别的任务预约（不加锁）
                if (_reservationManager.IsReserved(candidate.Source.Id)
                    || _reservationManager.IsReserved(candidate.Target.Id))
                {
                    continue;   // 设备被别的任务预约 → 试下一台车
                }

                // 判断通过 → 派发：只登记任务归属，**不占用任何锁**
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
                crane.Runtime.Step = TransportStep.AcquireResources;   // 第一步：状态机执行时获取路权+预约
                task.Runtime.TaskState = WorkpieceState.Assigned;
                usedCranes.Add(crane.Id);

                _logger.Info(LogCategory.Scheduling,
                    $"派发：任务 {Short(task.Id)} {candidate.Source.Name}→{candidate.Target.Name} 给 {crane.Name}（区间 {interval}，待状态机执行时取路权/预约）");
                break;   // 该候选已派人，处理下一个候选
            }
        }
    }

    /// <summary>
    /// 计算某候选用"最近的可执行天车到源"的距离，用于候选排序的 tie-break。
    /// 无可执行天车时返回 <see cref="double.MaxValue"/>（排在最后）。
    /// </summary>
    /// <param name="candidate">候选任务</param>
    /// <param name="cranes">天车快照</param>
    /// <param name="statuses">天车号 → 本轮状态</param>
    /// <returns>最近距离</returns>
    private static double NearestCraneDistance(CandidateTask candidate, IReadOnlyList<Crane> cranes, IReadOnlyDictionary<int, CraneStatusDto?> statuses)
    {
        var best = double.MaxValue;
        foreach (var crane in cranes)
        {
            if (!crane.IsEnabled || !crane.IsIdle
                || !crane.Covers(candidate.Source.PositionX) || !crane.Covers(candidate.Target.PositionX)
                || !statuses.TryGetValue(crane.Id, out var s) || s is null || s.HasAlarm)
            {
                continue;
            }

            var distance = Math.Abs(s.XDisplay - candidate.Source.PositionX);
            if (distance < best)
            {
                best = distance;
            }
        }

        return best;
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
