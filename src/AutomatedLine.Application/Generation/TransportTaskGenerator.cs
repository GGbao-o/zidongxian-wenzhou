using System.Diagnostics;
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Line;
using AutomatedLine.Application.Logging;
using AutomatedLine.Application.Reservations;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Domain.Diagnostics;
using AutomatedLine.Domain.Routing;
using AutomatedLine.Domain.Tasks;
using AutomatedLine.Domain.Workpieces;
using Microsoft.Extensions.Hosting;

namespace AutomatedLine.Application.Generation;

/// <summary>
/// 候选任务生成器（架构文档 §8.3）。
/// <para>
/// 每周期按当前内存状态与设备 PLC 信号**整体重建**候选任务池（Snapshot / Rebuild）。
/// 只在**线路运行中**（<see cref="LineControlService.IsRunning"/>）生成；线路未启动/暂停时清空候选池。
/// </para>
/// <para>
/// 生成规则：
/// <list type="number">
/// <item>只处理状态为 <see cref="WorkpieceState.Waiting"/> 的工件（未启动/暂停/已派发等一律跳过）；</item>
/// <item>源 = 工件当前所在设备（队列中的工件**只取队头**，源为唯一上料架）；</item>
/// <item>下一工序由 <see cref="ProcessRoute.GetNextAvailable"/> 现算（**跳过没有启用设备的节点**）；</item>
/// <item>目标 = 同类型设备中**全部**满足<连接/未预约/未报警/加工范围/放料就绪>者（全枚举，不预先选最优）。</item>
/// </list>
/// </para>
/// </summary>
public sealed class TransportTaskGenerator : IHostedService
{
    /// <summary>工件任务内存总表。</summary>
    private readonly IRuntimeTaskManager _taskManager;

    /// <summary>设备注册表。</summary>
    private readonly DeviceRegistry _deviceRegistry;

    /// <summary>候选任务池。</summary>
    private readonly CandidateTaskPool _candidatePool;

    /// <summary>设备预约管理器（排除已被其他任务预约的源/目标）。</summary>
    private readonly ResourceReservationManager _reservationManager;

    /// <summary>线路控制（只有运行中才生成）。</summary>
    private readonly LineControlService _lineControl;

    /// <summary>配置服务。</summary>
    private readonly IConfigService _configService;

    /// <summary>日志。</summary>
    private readonly IAppLogger _logger;

    /// <summary>"否决原因"变化日志：逐工件/逐设备的原因只在**变化**时落盘（避免 0.5s 一轮刷屏，又不漏"谁不行、为什么"）。</summary>
    private readonly ReasonChangeLog _reasonLog;

    /// <summary>内置工艺路线。</summary>
    private readonly ProcessRoute _route = ProcessRoute.CreateDefault();

    /// <summary>取消源。</summary>
    private CancellationTokenSource? _cts;

    /// <summary>循环任务。</summary>
    private Task? _loop;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="taskManager">工件任务总表</param>
    /// <param name="deviceRegistry">设备注册表</param>
    /// <param name="candidatePool">候选任务池</param>
    /// <param name="reservationManager">预约管理器</param>
    /// <param name="lineControl">线路控制</param>
    /// <param name="configService">配置服务</param>
    /// <param name="logger">日志</param>
    public TransportTaskGenerator(
        IRuntimeTaskManager taskManager,
        DeviceRegistry deviceRegistry,
        CandidateTaskPool candidatePool,
        ResourceReservationManager reservationManager,
        LineControlService lineControl,
        IConfigService configService,
        IAppLogger logger)
    {
        _taskManager = taskManager;
        _deviceRegistry = deviceRegistry;
        _candidatePool = candidatePool;
        _reservationManager = reservationManager;
        _lineControl = lineControl;
        _configService = configService;
        _logger = logger;
        // 最小落盘间隔复用日志抽样间隔（默认 20s）：原因抖动时最多每 20s 一条，不会反过来刷屏
        _reasonLog = new ReasonChangeLog(logger, LogCategory.Generation, configService.GetAppOptions().Logging.SampleSeconds);
    }

    /// <summary>启动生成循环。</summary>
    /// <param name="cancellationToken">宿主取消令牌</param>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        Console.WriteLine("[TransportTaskGenerator] 候选任务生成循环已启动");
        return Task.CompletedTask;
    }

    /// <summary>停止生成循环。</summary>
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

    /// <summary>生成主循环：线路运行中才重建候选池，否则清空。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        //从配置中拿候选任务生成周期 500ms   是从配置中拿 但是只拿一次 后面进入while  一直用的这个值
        var interval = Math.Max(50, _configService.GetAppOptions().Scheduling.GenerateIntervalMs);

        // 周期级"开始/完成"日志节流：约每 10s 打一对（轮数按实际周期换算，改配置也不会把日志刷爆）。
        // 线路未运行的轮次不打这一对，仍由下面的 generate-idle 抽样日志代表。
        var verboseEvery = Math.Max(1, 10_000 / interval);
        var cycle = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            // 每轮开头结算一轮"否决原因"：上一轮有问题、本轮没再登记的 → 写一条"已恢复"
            // （放在 IsRunning 判断之前，线路停了也照常结算）
            _reasonLog.BeginCycle();
            try
            {
                //生成候选任务就会判断 线路是否启动 如果未启动就不会生成 并且会清空候选池
                if (_lineControl.IsRunning)
                {
                    //生成候选任务
                    GenerateOnce(cycle++ % verboseEvery == 0);
                }
                //线路没有启动 清空候选任务池
                else if (!_candidatePool.IsEmpty)
                {
                    // 线路未启动/暂停：不生成，清空候选池（采集仍在后台进行）。
                    // 仅在确有内容时才 Clear，避免每周期空清空触发无谓的界面刷新。
                    _candidatePool.Clear();
                    _logger.Sample(LogCategory.Generation, "generate-idle", "线路未运行，候选池已清空");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(LogCategory.Generation, "候选任务生成异常", ex);
            }

            try
            {
                //延迟500ms
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>执行一次候选任务重建。</summary>
    /// <param name="verbose">是否输出本轮"开始/完成"日志（按周期节流，见 <see cref="RunAsync"/>）</param>
    private void GenerateOnce(bool verbose)
    {
        var startedTimestamp = Stopwatch.GetTimestamp();
        var devices = _deviceRegistry.Snapshot();

        // 工序是否参与工艺的唯一判据（与主页面"下一道工序"、工序展示页共用），本周期构造一次
        var hasDevice = ProcessRoute.AvailableDeviceFilter(devices);

        // 内存任务表里**既有**"主页面添加但未启动"的任务，**也有**"已启动入线"的任务：
        //   · 添加（未启动 Created）→ 只是列表上展示，不入队、不生成任务；
        //   · 启动（Waiting）→ 才算"入线任务"，参与候选生成；
        //   · 暂停(Paused)/已派发/执行中/完成/丢弃 → 也都不在线。
        // 这里先只取"入线任务"（= 已启动并排队中），后面所有逻辑都只针对它们。
        var lineTasks = _taskManager.Snapshot()
            .Where(t => t.Runtime.TaskState == WorkpieceState.Waiting)
            .ToList();

        var candidates = new List<CandidateTask>();

        // 上料架队头：在"入线任务"中取"在队列、入线顺序号最小"者
        // （顺序号在"启动"时分配，暂停清零、再启动重新入队尾）。
        //找的是在等待队列（刚录入内存，尚未上线）。的任务
        var queueHead = lineTasks
            .Where(t => t.Runtime.CurrentLocation.Kind == WorkpieceLocationKind.Queue)
            .OrderBy(t => t.Runtime.QueueOrder)
            .FirstOrDefault();

        // 上料队头描述：开始/完成两条日志共用，避免两处各算一次  只是用于日志输出 不管
        var headText = queueHead is null ? "无" : Describe(queueHead);

        if (verbose)
        {
            _logger.Info(LogCategory.Generation,
                $"生成开始：入线任务 {lineTasks.Count} 个，上料队头 {headText}");
        }

        // 只遍历入线任务（已过滤掉未启动/暂停/派发/完成等）
        foreach (var task in lineTasks)
        {
            //得到入线工件的运行状态
            var runtime = task.Runtime;

            //解析工件的源设备并校验源就绪
            var source = ResolveSource(task, queueHead, devices, out var sourceReason);
            if (source is null)
            {
                // 逐工件记录"为什么这个任务不行"：**变化时才落盘**（首次立即、恢复也报），不再按时间抽样——
                // 否则"部分工件不行"时看不出到底卡在哪台设备/哪个信号（详见 ReasonChangeLog）
                _reasonLog.Report($"gen-src-{task.Id}", $"工件 {Describe(task)}", sourceReason,
                    $"工件 {Describe(task)} 无候选：源不可用（{sourceReason}）");
                continue;
            }

            // 下一道实际可执行工序（跳过没有启用设备的节点）。
            // 队列中的新工件 CurrentProcess 还是 Unknown，等价于"处于上料工序"，从它往后找第一道可执行工序。
            var currentProcess = runtime.CurrentProcess == ProcessType.Unknown ? ProcessType.Loading : runtime.CurrentProcess;
            //GetNextAvailable 会跳过没有启用设备的工艺节点
            var next = _route.GetNextAvailable(currentProcess, hasDevice);
            if (next is null)
            {
                // 后面已无可执行工序：视为走完
                runtime.TaskState = WorkpieceState.Completed;
                _logger.Info(LogCategory.Generation, $"工件 {Describe(task)} 已无可执行工序，标记完成");
                // task.txt：另一条完成路径（线上已无该工件的下一道可执行工序）。
                // Generation 记"为什么判完成"，Task 记"生命周期终点"，两者互补不重复。
                _logger.Info(LogCategory.Task, $"任务完成：工件 {Describe(task)}（后续已无可执行工序）");
                continue;
            }

            // 目标全枚举：所有满足条件的同类设备各生成一个候选
            var info = task.Info;
            var matched = 0;
            var targetSkips = new List<string>();
            //过滤设备  找当前任务对应的下一个设备是什么
            foreach (var target in devices)
            {
                if (target.Type != next.DeviceType)
                {
                    continue;   // 只看下一道工序对应的设备类型
                }
            
                // 逐个收集"被排除的原因"，全部排除时汇总输出，帮助判断是设备没连线还是条件没到
                if (!target.IsEnabled) { targetSkips.Add($"{target.Name}:未启用"); continue; }
                if (target.Id == source.Id) { targetSkips.Add($"{target.Name}:与源相同"); continue; }
                if (!target.IsConnected) { targetSkips.Add($"{target.Name}:未连接"); continue; }
                if (_reservationManager.IsReserved(target.Id)) { targetSkips.Add($"{target.Name}:已被预约"); continue; }
                if (DeviceHandshakeRules.IsAlarm(target)) { targetSkips.Add($"{target.Name}:报警"); continue; }
                if (!target.CanProcess(info.Diameter, info.Length)) { targetSkips.Add($"{target.Name}:超出加工范围"); continue; }
                if (!DeviceHandshakeRules.CanReceive(target)) { targetSkips.Add($"{target.Name}:{ReceiveHint(target)}"); continue; }

                candidates.Add(new CandidateTask(task, source, target, priority: 100, targetProcess: next.Process));
                matched++;
            }

            // 逐目标记录"为什么这个设备不行"：**只要本轮有设备被跳过就报**（不再等"全军覆没"），
            // 这样"3 个目标跳过 2 个"时也能看到被跳过那两台各自的原因（未连接/已被预约/报警/超范围/握手未就绪）
            var skipReason = targetSkips.Count > 0
                ? string.Join("，", targetSkips)
                : matched == 0 ? $"无 {next.DeviceType} 类型设备" : null;
            if (skipReason is not null)
            {
                _reasonLog.Report($"gen-tgt-{task.Id}", $"工件 {Describe(task)}", skipReason,
                    $"工件 {Describe(task)} 下一道 {next.Process}({next.DeviceType}) 目标筛选：{skipReason}"
                    + (matched == 0 ? "（本道无可用目标）" : $"（另有 {matched} 台可用，已生成候选）"));
            }
        }
        //用新一批候选任务整体替换旧内容（Rebuild）。
        _candidatePool.Replace(candidates);

        if (verbose)
        {
            // 耗时用 Stopwatch 取：Windows 上 DateTime 精度约 15ms，测不出毫秒级周期
            _logger.Info(LogCategory.Generation,
                $"生成完成：候选 {candidates.Count} 个（入线任务 {lineTasks.Count}，上料队头 {headText}，耗时 {(long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds}ms）");
        }
    }

    /// <summary>
    /// 解析工件的源设备并校验源就绪。
    /// <list type="bullet">
    /// <item>在设备上：源=该设备；要求内存一致（设备当前工件就是本任务）、已连接、未预约、未报警、可取料；</item>
    /// <item>在队列：仅队头；源=唯一上料架；要求已连接、未预约、未报警、有板。</item>
    /// </list>
    /// </summary>
    /// <param name="task">工件任务</param>
    /// <param name="queueHead">当前队列队头（仅队头可上料）</param>
    /// <param name="devices">设备快照</param>
    /// <param name="reason">失败原因（供日志排查；成功为空串）</param>
    /// <returns>源设备；不满足返回 null</returns>
    private DeviceBase? ResolveSource(WorkpieceTask task, WorkpieceTask? queueHead, IReadOnlyList<DeviceBase> devices, out string reason)
    {
        var location = task.Runtime.CurrentLocation;

        if (location.IsOnDevice && location.ResourceId is int deviceId)//工件在设备上
        {
            var device = devices.FirstOrDefault(d => d.Id == deviceId && d.IsEnabled);
            if (device is null)
            {
                reason = $"所在设备 {deviceId} 不存在或未启用"; return null;
            }
            //判断设备当前任务是否是这个任务
            if (device.CurrentWorkpiece != task)
            {
                reason = $"设备 {device.Name} 上工件与任务不一致"; return null;
            }   
            //判断设备是否连接
            if (!device.IsConnected)
            {
                reason = $"设备 {device.Name} 未连接"; return null;
            }
            //判断设备是否预约
            if (_reservationManager.IsReserved(device.Id))
            {
                reason = $"设备 {device.Name} 已被预约"; return null;
            }
            //判断设备是否报警 (这个是只有研磨机)
            if (DeviceHandshakeRules.IsAlarm(device))
            {
                reason = $"设备 {device.Name} 报警"; return null;
            }
            //判断源设备是否可取料
            if (!DeviceHandshakeRules.CanProvide(device))
            {
                reason = $"设备 {device.Name} 未就绪（{ProvideHint(device)}）"; return null;
            }

            reason = string.Empty;
            return device;
        }

        if (location.Kind == WorkpieceLocationKind.Queue)//在等待队列（刚录入内存，尚未上线）。 工件在上料架
        {
            // 只有队头才生成上料候选；上料架唯一
            if (!ReferenceEquals(task, queueHead))
            {
                reason = "非队列队头（需前面的工件先上料）";
                return null;
            }

            var rack = devices.FirstOrDefault(d => d.Type == DeviceType.LoadRack && d.IsEnabled);
            if (rack is null) { reason = "无启用的上料架"; return null; }
            if (!rack.IsConnected) { reason = $"上料架 {rack.Name} 未连接"; return null; }
            if (_reservationManager.IsReserved(rack.Id)) { reason = $"上料架 {rack.Name} 已被预约"; return null; }
            if (DeviceHandshakeRules.IsAlarm(rack)) { reason = $"上料架 {rack.Name} 报警"; return null; }
            if (!DeviceHandshakeRules.CanProvide(rack)) { reason = $"上料架 {rack.Name} 无板（HasMaterial=false）"; return null; }

            reason = string.Empty;
            return rack;
        }

        reason = $"工件位置异常（{location}）";
        return null;
    }

    /// <summary>把"源未就绪"翻译成具体缺失的握手信号名，便于日志排查。</summary>
    private static string ProvideHint(DeviceBase device) => device.Type switch
    {
        DeviceType.GrinderMachine when DeviceHandshakeRules.AutomaticModeBlockReason(device) is { } reason => reason,
        DeviceType.LoadRack => "HasMaterial=false",
        DeviceType.BoringMachine or DeviceType.LatheMachine or DeviceType.GrinderMachine => "RequestUnload=false",
        DeviceType.MarkerMachine => "ProcessDone=false",
        _ => "无可取料规则"
    };

    /// <summary>把"目标不可放料"翻译成具体缺失的握手信号名，便于日志排查。</summary>
    private static string ReceiveHint(DeviceBase device) => device.Type switch
    {
        DeviceType.GrinderMachine when DeviceHandshakeRules.AutomaticModeBlockReason(device) is { } reason => reason,
        DeviceType.BoringMachine or DeviceType.LatheMachine or DeviceType.GrinderMachine => "RequestData=false",
        DeviceType.MarkerMachine => "Busy=true（设备忙碌）",
        DeviceType.UnloadRack => "有板（不可放）或无采集数据",
        _ => "无放料规则"
    };

    /// <summary>工件日志短文本（版号/序号）。</summary>
    private static string Describe(WorkpieceTask task) => task.Info.ToString();

}
