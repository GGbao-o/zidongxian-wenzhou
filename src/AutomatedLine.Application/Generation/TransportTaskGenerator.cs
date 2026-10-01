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
        var interval = Math.Max(50, _configService.GetAppOptions().Scheduling.GenerateIntervalMs);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_lineControl.IsRunning)
                {
                    GenerateOnce();
                }
                else
                {
                    // 线路未启动/暂停：不生成，清空候选池（采集仍在后台进行）
                    _candidatePool.Clear();
                }
            }
            catch (Exception ex)
            {
                _logger.Error(LogCategory.Generation, "候选任务生成异常", ex);
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

    /// <summary>执行一次候选任务重建。</summary>
    private void GenerateOnce()
    {
        var devices = _deviceRegistry.Snapshot();
        var tasks = _taskManager.Snapshot();
        var candidates = new List<CandidateTask>();

        // 队列队头：状态 Waiting、在队列、且入线顺序号最小（序号在"启动"时分配，暂停清零、再启动重新入队尾）。
        var queueHead = tasks
            .Where(t => t.Runtime.TaskState == WorkpieceState.Waiting
                        && t.Runtime.CurrentLocation.Kind == WorkpieceLocationKind.Queue)
            .OrderBy(t => t.Runtime.QueueOrder)
            .FirstOrDefault();

        foreach (var task in tasks)
        {
            var runtime = task.Runtime;

            // 只处理"排队中"的工件；未启动/暂停/已派发/执行中/完成/丢弃 全部跳过
            if (runtime.TaskState != WorkpieceState.Waiting)
            {
                continue;
            }

            var source = ResolveSource(task, queueHead, devices);
            if (source is null)
            {
                continue;
            }

            // 下一道实际可执行工序（跳过没有启用设备的节点）。
            // 队列中的新工件 CurrentProcess 还是 Unknown，等价于"处于上料工序"，从它往后找第一道可执行工序。
            var currentProcess = runtime.CurrentProcess == ProcessType.Unknown ? ProcessType.Loading : runtime.CurrentProcess;
            var next = _route.GetNextAvailable(currentProcess, type => HasUsableDevice(devices, type));
            if (next is null)
            {
                // 后面已无可执行工序：视为走完
                runtime.TaskState = WorkpieceState.Completed;
                continue;
            }

            // 目标全枚举：所有满足条件的同类设备各生成一个候选
            var info = task.Info;
            foreach (var target in devices)
            {
                if (target.Type != next.DeviceType
                    || !target.IsEnabled
                    || target.Id == source.Id
                    || !target.IsConnected
                    || _reservationManager.IsReserved(target.Id)
                    || DeviceHandshakeRules.IsAlarm(target)
                    || !target.CanProcess(info.Diameter, info.Length)
                    || !DeviceHandshakeRules.CanReceive(target))
                {
                    continue;
                }

                candidates.Add(new CandidateTask(task, source, target, priority: 100, targetProcess: next.Process));
            }
        }

        _candidatePool.Replace(candidates);

        // 高频循环，成功日志按抽样间隔落盘
        _logger.Sample(LogCategory.Generation, "generate-cycle", $"候选任务重建完成，共 {candidates.Count} 个");
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
    /// <returns>源设备；不满足返回 null</returns>
    private DeviceBase? ResolveSource(WorkpieceTask task, WorkpieceTask? queueHead, IReadOnlyList<DeviceBase> devices)
    {
        var location = task.Runtime.CurrentLocation;

        if (location.IsOnDevice && location.ResourceId is int deviceId)
        {
            var device = devices.FirstOrDefault(d => d.Id == deviceId && d.IsEnabled);
            if (device is null
                || device.CurrentWorkpiece != task      // 内存一致：源设备上就是该工件
                || !device.IsConnected
                || _reservationManager.IsReserved(device.Id)
                || DeviceHandshakeRules.IsAlarm(device)
                || !DeviceHandshakeRules.CanProvide(device))
            {
                return null;
            }

            return device;
        }

        if (location.Kind == WorkpieceLocationKind.Queue)
        {
            // 只有队头才生成上料候选；上料架唯一
            if (!ReferenceEquals(task, queueHead))
            {
                return null;
            }

            var rack = devices.FirstOrDefault(d => d.Type == DeviceType.LoadRack && d.IsEnabled);
            if (rack is null
                || !rack.IsConnected
                || _reservationManager.IsReserved(rack.Id)
                || DeviceHandshakeRules.IsAlarm(rack)
                || !DeviceHandshakeRules.CanProvide(rack))   // 有板
            {
                return null;
            }

            return rack;
        }

        return null;
    }

    /// <summary>
    /// 判断某设备类型当前是否存在启用设备，用于决定工序节点是否跳过。
    /// </summary>
    /// <param name="devices">设备快照</param>
    /// <param name="type">设备类型</param>
    /// <returns>存在启用设备返回 true</returns>
    private static bool HasUsableDevice(IReadOnlyList<DeviceBase> devices, DeviceType type)
        => devices.Any(d => d.Type == type && d.IsEnabled);
}
