using AutomatedLine.Application.Abstractions;
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
/// 每周期根据当前内存状态**整体重建**候选任务池（Snapshot / Rebuild），不维护传统任务队列。
/// 生成规则（P3 初版，现场可细化）：
/// <list type="number">
/// <item>只处理未派发/未执行的工件任务；</item>
/// <item>源 = 工件当前所在设备；若在队列（尚未上线）则以启用的上料架为源；</item>
/// <item>下一工序由 <see cref="ProcessRoute.GetNext"/> 现算；走完最后一道则标记完成；</item>
/// <item>目标 = 同类设备中"启用、未被预约、加工范围匹配"里，优先空闲、再取离源最近的；</item>
/// </list>
/// </summary>
public sealed class TransportTaskGenerator : IHostedService
{
    /// <summary>工件任务内存总表。</summary>
    private readonly IRuntimeTaskManager _taskManager;

    /// <summary>设备注册表。</summary>
    private readonly DeviceRegistry _deviceRegistry;

    /// <summary>候选任务池。</summary>
    private readonly CandidateTaskPool _candidatePool;

    /// <summary>设备预约管理器（用于排除已被预约的目标）。</summary>
    private readonly ResourceReservationManager _reservationManager;

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
    /// <param name="configService">配置服务</param>
    /// <param name="logger">日志</param>
    public TransportTaskGenerator(
        IRuntimeTaskManager taskManager,
        DeviceRegistry deviceRegistry,
        CandidateTaskPool candidatePool,
        ResourceReservationManager reservationManager,
        IConfigService configService,
        IAppLogger logger)
    {
        _taskManager = taskManager;
        _deviceRegistry = deviceRegistry;
        _candidatePool = candidatePool;
        _reservationManager = reservationManager;
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

    /// <summary>生成主循环。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var interval = Math.Max(50, _configService.GetAppOptions().Scheduling.GenerateIntervalMs);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                GenerateOnce();
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
        var candidates = new List<CandidateTask>();

        foreach (var task in _taskManager.Snapshot())
        {
            var runtime = task.Runtime;

            // 已派发/执行中/已完成/已丢弃的不再生成
            if (runtime.TaskState is WorkpieceState.Assigned or WorkpieceState.Running
                or WorkpieceState.Completed or WorkpieceState.Discarded)
            {
                continue;
            }

            var source = ResolveSource(runtime, devices);
            if (source is null)
            {
                continue;
            }

            // 源设备正被别的工件占用则跳过
            if (source.CurrentWorkpiece is not null && source.CurrentWorkpiece != task)
            {
                continue;
            }

            // 计算下一道可执行工序：可选工序缺设备时自动跳过（§3.1 设备允许缺失）
            var next = _route.GetNextAvailable(runtime.CurrentProcess, type => HasUsableDevice(devices, type));
            if (next is null)
            {
                // 已是最后一道工序：视为完成，退出候选
                runtime.TaskState = WorkpieceState.Completed;
                continue;
            }

            var target = SelectTarget(next, task, source, devices);
            if (target is null)
            {
                continue;
            }

            candidates.Add(new CandidateTask(task, source, target, priority: 100, targetProcess: next.Process));
        }

        _candidatePool.Replace(candidates);

        // 高频循环，成功日志按抽样间隔落盘
        _logger.Sample(LogCategory.Generation, "generate-cycle", $"候选任务重建完成，共 {candidates.Count} 个");
    }

    /// <summary>
    /// 解析工件当前的源设备：在设备上则取该设备；在队列（未上线）则取启用的上料架。
    /// </summary>
    /// <param name="runtime">工件运行态</param>
    /// <param name="devices">设备快照</param>
    /// <returns>源设备；无法确定返回 null</returns>
    private static DeviceBase? ResolveSource(WorkpieceRuntime runtime, IReadOnlyList<DeviceBase> devices)
    {
        if (runtime.CurrentLocation.IsOnDevice && runtime.CurrentLocation.ResourceId is int deviceId)
        {
            return devices.FirstOrDefault(d => d.Id == deviceId);
        }

        if (runtime.CurrentLocation.Kind == WorkpieceLocationKind.Queue)
        {
            return devices.FirstOrDefault(d => d.Type == DeviceType.LoadRack && d.IsEnabled);
        }

        return null;
    }

    /// <summary>
    /// 判断某设备类型当前是否存在可用（启用）设备，用于决定可选工序是否跳过。
    /// </summary>
    /// <param name="devices">设备快照</param>
    /// <param name="type">设备类型</param>
    /// <returns>存在启用设备返回 true</returns>
    private static bool HasUsableDevice(IReadOnlyList<DeviceBase> devices, DeviceType type)
        => devices.Any(d => d.Type == type && d.IsEnabled);

    /// <summary>
    /// 选择目标设备：同类型、启用、未被预约、加工范围匹配；优先空闲，再取离源最近。
    /// </summary>
    /// <param name="node">目标工序节点</param>
    /// <param name="task">工件任务</param>
    /// <param name="source">源设备</param>
    /// <param name="devices">设备快照</param>
    /// <returns>目标设备；无可用返回 null</returns>
    private DeviceBase? SelectTarget(ProcessNode node, WorkpieceTask task, DeviceBase source, IReadOnlyList<DeviceBase> devices)
    {
        var info = task.Info;

        return devices
            .Where(d => d.Type == node.DeviceType
                        && d.IsEnabled
                        && d.Id != source.Id
                        && !_reservationManager.IsReserved(d.Id)
                        && d.CanProcess(info.Diameter, info.Length))
            .OrderBy(d => d.ResourceState == ResourceState.Available ? 0 : 1)
            .ThenBy(d => Math.Abs(d.PositionX - source.PositionX))
            .FirstOrDefault();
    }
}
