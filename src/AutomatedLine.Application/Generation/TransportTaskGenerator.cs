using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Line;
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
        //从配置中拿候选任务生成周期 500ms
        var interval = Math.Max(50, _configService.GetAppOptions().Scheduling.GenerateIntervalMs);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                //生成候选任务就会判断 线路是否启动 如果未启动就不会生成 并且会清空候选池
                if (_lineControl.IsRunning)
                {
                    //生成候选任务
                    GenerateOnce();
                }
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
    private void GenerateOnce()
    {
        var devices = _deviceRegistry.Snapshot();

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
        var queueHead = lineTasks
            .Where(t => t.Runtime.CurrentLocation.Kind == WorkpieceLocationKind.Queue)
            .OrderBy(t => t.Runtime.QueueOrder)
            .FirstOrDefault();

        // 只遍历入线任务（已过滤掉未启动/暂停/派发/完成等）
        foreach (var task in lineTasks)
        {
            var runtime = task.Runtime;

            //解析工件的源设备并校验源就绪
            var source = ResolveSource(task, queueHead, devices, out var sourceReason);
            if (source is null)
            {
                // 逐工件抽样记录"为什么没有候选"，便于现场排查（同一工件每抽样间隔最多一条）
                _logger.Sample(LogCategory.Generation, $"gen-nosource-{task.Id}",
                    $"工件 {Describe(task)} 无候选：源不可用（{sourceReason}）");
                continue;
            }

            // 下一道实际可执行工序（跳过没有启用设备的节点）。
            // 队列中的新工件 CurrentProcess 还是 Unknown，等价于"处于上料工序"，从它往后找第一道可执行工序。
            var currentProcess = runtime.CurrentProcess == ProcessType.Unknown ? ProcessType.Loading : runtime.CurrentProcess;
            //GetNextAvailable 会跳过没有启用设备的工艺节点
            var next = _route.GetNextAvailable(currentProcess, type => HasUsableDevice(devices, type));
            if (next is null)
            {
                // 后面已无可执行工序：视为走完
                runtime.TaskState = WorkpieceState.Completed;
                _logger.Info(LogCategory.Generation, $"工件 {Describe(task)} 已无可执行工序，标记完成");
                continue;
            }

            // 目标全枚举：所有满足条件的同类设备各生成一个候选
            var info = task.Info;
            var matched = 0;
            var targetSkips = new List<string>();
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

            if (matched == 0)
            {
                var detail = targetSkips.Count == 0 ? $"无 {next.DeviceType} 类型设备" : string.Join("，", targetSkips);
                _logger.Sample(LogCategory.Generation, $"gen-notarget-{task.Id}",
                    $"工件 {Describe(task)} 下一道 {next.Process}({next.DeviceType}) 无可用目标：{detail}");
            }
        }

        _candidatePool.Replace(candidates);

        // 高频循环，成功日志按抽样间隔落盘（含入线任务数、上料队头、候选数）
        var headText = queueHead is null ? "无" : Describe(queueHead);
        _logger.Sample(LogCategory.Generation, "generate-cycle",
            $"候选重建：入线任务 {lineTasks.Count}，上料队头 {headText}，候选 {candidates.Count} 个");
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

        if (location.IsOnDevice && location.ResourceId is int deviceId)
        {
            var device = devices.FirstOrDefault(d => d.Id == deviceId && d.IsEnabled);
            if (device is null) { reason = $"所在设备 {deviceId} 不存在或未启用"; return null; }
            if (device.CurrentWorkpiece != task) { reason = $"设备 {device.Name} 上工件与任务不一致"; return null; }   // 内存一致
            if (!device.IsConnected) { reason = $"设备 {device.Name} 未连接"; return null; }
            if (_reservationManager.IsReserved(device.Id)) { reason = $"设备 {device.Name} 已被预约"; return null; }
            if (DeviceHandshakeRules.IsAlarm(device)) { reason = $"设备 {device.Name} 报警"; return null; }
            if (!DeviceHandshakeRules.CanProvide(device)) { reason = $"设备 {device.Name} 未就绪（{ProvideHint(device)}）"; return null; }

            reason = string.Empty;
            return device;
        }

        if (location.Kind == WorkpieceLocationKind.Queue)
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
        DeviceType.LoadRack => "HasMaterial=false",
        DeviceType.BoringMachine or DeviceType.LatheMachine or DeviceType.GrinderMachine => "RequestUnload=false",
        DeviceType.MarkerMachine => "ProcessDone=false",
        _ => "无可取料规则"
    };

    /// <summary>把"目标不可放料"翻译成具体缺失的握手信号名，便于日志排查。</summary>
    private static string ReceiveHint(DeviceBase device) => device.Type switch
    {
        DeviceType.BoringMachine or DeviceType.LatheMachine or DeviceType.GrinderMachine => "RequestData=false",
        DeviceType.MarkerMachine => "Idle=false",
        DeviceType.UnloadRack => "有板（不可放）或无采集数据",
        _ => "无放料规则"
    };

    /// <summary>工件日志短文本（版号/序号）。</summary>
    private static string Describe(WorkpieceTask task) => task.Info.ToString();

    /// <summary>
    /// 判断某设备类型当前是否存在启用设备，用于决定工序节点是否跳过。
    /// </summary>
    /// <param name="devices">设备快照</param>
    /// <param name="type">设备类型</param>
    /// <returns>存在启用设备返回 true</returns>
    private static bool HasUsableDevice(IReadOnlyList<DeviceBase> devices, DeviceType type)
        => devices.Any(d => d.Type == type && d.IsEnabled);
}
