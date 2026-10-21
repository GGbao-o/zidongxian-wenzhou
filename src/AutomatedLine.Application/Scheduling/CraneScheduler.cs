using System.Diagnostics;
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Collection;
using AutomatedLine.Application.Generation;
using AutomatedLine.Application.Line;
using AutomatedLine.Application.Logging;
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
/// 派发只做只读判断（路权/预约/空闲等），通过后把任务写入**该车任务队列**（<see cref="Runtime.CraneTaskChannels"/>）：
/// **不碰 <c>Crane.Runtime</c>**，天车空闲/任务/执行由该车 Worker 独占决定。
/// 真正的路权/预约获取在状态机执行时完成，拿不到则回滚。
/// </summary>
public sealed class CraneScheduler : IHostedService
{
    /// <summary>候选任务池。</summary>
    private readonly CandidateTaskPool _candidatePool;

    /// <summary>每车任务队列（调度器→Worker 交接通道）。</summary>
    private readonly CraneTaskChannels _channels;

    /// <summary>天车注册表。</summary>
    private readonly CraneRegistry _craneRegistry;

    /// <summary>共享快照（只使用有效且未过期的状态）。</summary>
    private readonly ICraneStateProvider _craneStates;

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

    /// <summary>"否决原因"变化日志：逐候选/逐天车的原因只在**变化**时落盘（避免 0.5s 一轮刷屏，又不漏"谁不行、为什么"）。</summary>
    private readonly ReasonChangeLog _reasonLog;

    /// <summary>取消源。</summary>
    private CancellationTokenSource? _cts;

    /// <summary>循环任务。</summary>
    private Task? _loop;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="candidatePool">候选任务池</param>
    /// <param name="channels">每车任务队列</param>
    /// <param name="craneRegistry">天车注册表</param>
    /// <param name="craneStates">共享快照（只使用有效且未过期的状态）</param>
    /// <param name="rightOfWayManager">路权管理器</param>
    /// <param name="reservationManager">预约管理器</param>
    /// <param name="lineControl">线路控制</param>
    /// <param name="configService">配置服务</param>
    /// <param name="logger">日志</param>
    public CraneScheduler(
        CandidateTaskPool candidatePool,
        CraneTaskChannels channels,
        CraneRegistry craneRegistry,
        ICraneStateProvider craneStates,
        RightOfWayManager rightOfWayManager,
        ResourceReservationManager reservationManager,
        LineControlService lineControl,
        IConfigService configService,
        IAppLogger logger)
    {
        _candidatePool = candidatePool;
        _channels = channels;
        _craneRegistry = craneRegistry;
        _craneStates = craneStates;
        _rightOfWayManager = rightOfWayManager;
        _reservationManager = reservationManager;
        _lineControl = lineControl;
        _configService = configService;
        _logger = logger;
        // 最小落盘间隔复用日志抽样间隔（默认 20s）：原因抖动时最多每 20s 一条，不会反过来刷屏
        _reasonLog = new ReasonChangeLog(logger, LogCategory.Scheduling, configService.GetAppOptions().Logging.SampleSeconds);
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
        //集中式调度周期
        var interval = Math.Max(50, _configService.GetAppOptions().Scheduling.ScheduleIntervalMs);

        // 周期级"开始/完成"日志节流：约每 10s 打一对（轮数按实际周期换算，改配置也不会把日志刷爆）。
        // 空转轮次（候选池为空）不打这一对，仍由下面的 dispatch-empty 抽样日志代表。
        var verboseEvery = Math.Max(1, 10_000 / interval);
        var cycle = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            // 每轮开头结算一轮"否决原因"：上一轮有问题、本轮没再登记的 → 写一条"已恢复"
            // （放在 IsRunning 判断之前，线路停了也照常结算）
            _reasonLog.BeginCycle();
            try
            {
                //线路正在运行
                if (_lineControl.IsRunning)
                {
                    await DispatchOnceAsync(cycle++ % verboseEvery == 0, cancellationToken).ConfigureAwait(false);
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
    /// 做**只读判断**，通过即派发；真正的路权/预约获取由**状态机执行任务时**完成（见 <c>CraneStateMachine.Acquire</c>）。
    /// </para>
    /// <para>
    /// 竞态：判断通过后到状态机真正加锁之间有窗口，可能被别的车抢先；届时状态机 `TryAcquire` 失败会
    /// **回滚**（释放天车锁、任务回 Waiting），下一轮重新派发。
    /// </para>
    /// </summary>
    /// <param name="verbose">是否输出本轮"开始/完成"日志（按周期节流，见 <see cref="RunAsync"/>）</param>
    /// <param name="cancellationToken">取消令牌</param>
    private Task DispatchOnceAsync(bool verbose, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startedTimestamp = Stopwatch.GetTimestamp();
        var cranes = _craneRegistry.Snapshot();

        // 候选池为空时不必整理选车缓存，后台采集仍独立运行。
        var pool = _candidatePool.Snapshot();
        if (pool.Count == 0)
        {
            // 登记"候选池为空"：空↔非空各出一条，现场据此区分"没活干"与"有活但派不出去"
            _reasonLog.Report("sched-pool", "调度", "候选池为空", "候选池为空，本轮无任务可派");
            return Task.CompletedTask;
        }
        //只是记录日志
        if (verbose)
        {
            _logger.Info(LogCategory.Scheduling,
                $"调度开始：候选池 {pool.Count} 个，启用天车 {cranes.Count(c => c.IsEnabled)} 台");
        }

        // 启用且空闲的天车只读有效缓存；未采集、失败或过期均不参加本轮选车。
        var statuses = ReadIdleCraneStatuses(cranes);

        // 候选排序：优先级降序 → 距离最近（最近可执行天车到源的距离；无可执行天车的排最后）。不做负载均衡。  拿到的是个List集合
        var candidates = pool
            .OrderByDescending(c => c.Priority)
            .ThenBy(c => NearestCraneDistance(c, cranes, statuses))
            .ToList();
        //遍历候选任务
        var dispatchedCount = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var task = candidate.Workpiece;

            // 只派发仍在"排队中"的任务（可能已被本轮/其它周期派发，或已暂停/完成）
            if (task.Runtime.TaskState != WorkpieceState.Waiting)
            {
                continue;
            }

            //  针对每一个候选任务筛选天车：启用、空闲、覆盖两端、状态有效、无报警且下压信号明确未触发。
            // "一车一任务"由 IsIdle 保证：派发时立即置 State=Reserved，该车 IsIdle 当场变 false，
            // 后续候选自然排除它（Snapshot 是同一批 Crane 引用）。  
            //eligible 是“针对当前这个候选任务，所有有资格执行它的天车”列表，已按“离源最近”排好序。 判断是否启动  IsIdle是否空闲(CraneStated的状态和Runtime.CurrentTask)   决定当前任务先尝试哪台天车。
            //把所有满足条件的天车都筛选出来，然后按照距离源工位从近到远排序。
            var eligible = cranes   
                .Where(c => c.IsEnabled
                            && c.IsIdle
                            && c.Covers(candidate.Source.PositionX)
                            && c.Covers(candidate.Target.PositionX)
                            && statuses.TryGetValue(c.Id, out var s) && s is not null && !s.HasAlarm
                            && s.PressureLimit == false) // null为读取未知，不能当作下压信号未亮。
                .OrderBy(c => Math.Abs(statuses[c.Id]!.XDisplay - candidate.Source.PositionX))   // 离源最近
                .ToList();
            
            // 逐候选记录"为什么这些天车不行"：**只要本轮有天车被排除就报**（不再等"全都没资格"），
            // 这样"2 台车里 1 台能干活"时，也能看到另一台被排除的原因（未启用/忙/范围不覆盖/读状态失败/报警）
            var craneReasons = ExplainNoEligible(candidate, cranes, statuses);
            //判断有没有天车被筛选排除，而不是判断有没有可执行天车。
            if (eligible.Count < cranes.Count)
            {
                _reasonLog.Report($"sched-crane-{task.Id}", $"候选 {Describe(candidate)}", craneReasons,
                    $"候选 {Describe(candidate)} 天车筛选：{craneReasons}"
                    + (eligible.Count == 0 ? "（无可执行天车）" : $"（可执行 {eligible.Count} 台）"));
            }
            //当前任务可执行的天车数量为0
            if (eligible.Count == 0)
            {
                continue;
            }

            
            // 逐台车尝试（**换车**）：某台车判断不通过就试下一台，绝不死等  找到一台能执行任务的天车
            var dispatched = false;
            string? blockReason = null;
            foreach (var crane in eligible)
            {
                var interval = candidate.GetRightOfWayInterval(crane.Width);   // 每车宽度不同 → 区间不同

                // 【只判断·路权】区间是否空闲（不加锁）
                if (!_rightOfWayManager.CanAcquire(interval))
                {
                    blockReason = $"路权区间 {interval} 与在途任务冲突";
                    continue;   // 区间被别的任务占用 → 试下一台车
                }

                // 【只判断·预约】源/目标是否已被别的任务预约（不加锁）
                if (_reservationManager.IsReserved(candidate.Source.Id)
                    || _reservationManager.IsReserved(candidate.Target.Id))
                {
                    blockReason = "源/目标设备已被其他任务预约";
                    continue;   // 设备被别的任务预约 → 试下一台车
                }

                // 判断通过 → 派发：只把任务写入该车队列（**不碰 Crane.Runtime**；天车状态由 Worker 独占管理）
                var transportTask = new TransportTask
                {
                    WorkpieceRuntimeId = task.Id,
                    SourceDeviceId = candidate.Source.Id,
                    TargetDeviceId = candidate.Target.Id,
                    SourceX = candidate.Source.PositionX,
                    TargetX = candidate.Target.PositionX,
                    TargetProcess = candidate.TargetProcess,
                    Priority = candidate.Priority
                };

                // 候选生成后采集器可能发布了新模式；入队前再读源/目标缓存，禁止旧候选绕过研磨机模式条件。
                var modeBlocked = DeviceHandshakeRules.AutomaticModeBlockReason(candidate.Source) is { } sourceMode
                    //modeBlocked为true   
                    ? $"源设备 {candidate.Source.Name}：{sourceMode}"
                    //modeBlocked为false   
                    : DeviceHandshakeRules.AutomaticModeBlockReason(candidate.Target) is { } targetMode
                        ? $"目标设备 {candidate.Target.Name}：{targetMode}" : null;
                //说明源设备或者目标设备有一个是研磨机而且研磨机不是自动模式
                if (modeBlocked is not null)
                {
                    blockReason = modeBlocked;
                    break; // 模式是设备条件，换一台天车也不能执行；工件仍保持Waiting。
                }

                // 【原子占位】Waiting → Assigned 只成功一次（防止同一任务被并发派发）   将这个任务已派发     仅当当前状态 == Waiting 时，才把它改成 Assigned已派发，返回 true；否则返回 false，不改动。
                if (!task.Runtime.TryTransition(WorkpieceState.Waiting, WorkpieceState.Assigned))
                {
                    blockReason = "任务已被其它周期派发";
                    break;   // 本轮无需再处理该候选
                }
                //写入天车队列
                if (!_channels.For(crane.Id).Writer.TryWrite(transportTask))
                {
                    // 入队失败：回退状态，换下一台车
                    task.Runtime.TaskState = WorkpieceState.Waiting;
                    blockReason = $"{crane.Name} 任务队列已满（已有一条待执行）";
                    continue;
                }

                _logger.Info(LogCategory.Scheduling,
                    $"派发：工件 {task.Info}（任务 {Short(task.Id)}）{candidate.Source.Name}→{candidate.Target.Name} 给 {crane.Name}（区间 {interval}，已入队待执行）");
                dispatched = true;
                dispatchedCount++;
                break;   // 该候选已派人，处理下一个候选
            }

            if (!dispatched)
            {
                // 有可执行天车，但路权/预约全冲突：本周期不派发，下轮用最新状态重算
                var blockedReason = blockReason ?? "无可执行天车";
                _reasonLog.Report($"sched-blocked-{task.Id}", $"候选 {Describe(candidate)}", blockedReason,
                    $"候选 {Describe(candidate)} 暂未派发：{blockedReason}（可选天车{eligible.Count}台，下轮重试）");
            }
        }

        if (verbose)
        {
            // 耗时用 Stopwatch 取：Windows 上 DateTime 精度约 15ms，测不出毫秒级周期
            _logger.Info(LogCategory.Scheduling,
                $"调度完成：候选 {candidates.Count} 个，本轮派发 {dispatchedCount} 个，耗时 {(long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds}ms");
        }
        return Task.CompletedTask;
    }

    /// <summary>候选任务日志短文本（工件 + 源→目标）。</summary>
    /// <param name="candidate">候选任务</param>
    /// <returns>描述文本</returns>
    private static string Describe(CandidateTask candidate)
        => $"{candidate.Workpiece.Info} {candidate.Source.Name}→{candidate.Target.Name}";

    /// <summary>
    /// 当某候选没有可执行天车时，逐台天车说明被排除的原因，供日志排查（如全忙/未连接/报警/范围不覆盖/读状态失败）。
    /// </summary>
    /// <param name="candidate">候选任务</param>
    /// <param name="cranes">天车快照</param>
    /// <param name="statuses">天车号 → 本轮状态</param>
    /// <returns>原因汇总文本</returns>
    private static string ExplainNoEligible(CandidateTask candidate, IReadOnlyList<Crane> cranes, IReadOnlyDictionary<int, CraneStatusDto?> statuses)
    {
        if (cranes.Count == 0)
        {
            return "无天车";
        }

        var reasons = new List<string>();
        foreach (var crane in cranes)
        {
            if (!crane.IsEnabled) { reasons.Add($"{crane.Name}:未启用"); continue; }
            if (!crane.IsIdle) { reasons.Add($"{crane.Name}:忙"); continue; }
            if (!crane.Covers(candidate.Source.PositionX) || !crane.Covers(candidate.Target.PositionX))
            {
                reasons.Add($"{crane.Name}:范围不覆盖"); continue;
            }
            if (!statuses.TryGetValue(crane.Id, out var status) || status is null)
            {
                reasons.Add($"{crane.Name}:状态不可用（未采集/失败/过期）"); continue;
            }
            if (status.HasAlarm) { reasons.Add($"{crane.Name}:报警"); continue; }
            if (status.PressureLimit != false)
            {
                reasons.Add($"{crane.Name}:下压信号{(status.PressureLimit == true ? "已触发" : "未知")}");
                continue;
            }
            reasons.Add($"{crane.Name}:可执行");
        }

        return string.Join("，", reasons);
    }

    /// <summary>
    /// 计算某候选用"最近的可执行天车到源"的距离，用于候选排序的 tie-break。   它不是找“某一台”天车，而是找出“所有能执行这个候选的天车里，离源最近的那台，返回那个最近的距离值”。
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
                || !statuses.TryGetValue(crane.Id, out var s) || s is null || s.HasAlarm || s.PressureLimit != false)
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

    /// <summary>取得启用且空闲天车的有效共享状态，不在调度线程发起通信。</summary>
    /// <param name="cranes">当前天车目录快照。</param>
    /// <returns>天车号到有效状态的映射，不可用时为 null。</returns>
    private Dictionary<int, CraneStatusDto?> ReadIdleCraneStatuses(IReadOnlyList<Crane> cranes)
    {
        //调用的是CraneStateCollector后台采集拿到的缓存数据 
        return cranes.Where(c => c.IsEnabled && c.IsIdle)
            .ToDictionary(c => c.Id, c => _craneStates.GetFreshStatus(c.Id));
    }

    /// <summary>任务标识短文本，用于日志。</summary>
    /// <param name="taskId">任务标识</param>
    /// <returns>前 8 位</returns>
    private static string Short(Guid taskId) => taskId.ToString("N")[..8];
}
