using System.Collections.ObjectModel;
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Collection;
using AutomatedLine.Application.Reservations;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Domain.Workpieces;
using AutomatedLine.Ui.Helpers;
using AutomatedLine.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutomatedLine.Ui.ViewModels;

/// <summary>
/// 大屏展示页面 ViewModel（只读，无任何控制按钮）。
/// 展示全部设备与天车的实时信号与内存数据；设备/天车数量可能很多，界面必须支持横竖滚动
/// （架构文档 §13.6）。
/// </summary>
public partial class ScreenViewModel : ObservableObject
{
    /// <summary>设备注册表。</summary>
    private readonly DeviceRegistry _deviceRegistry;

    /// <summary>天车注册表。</summary>
    private readonly CraneRegistry _craneRegistry;

    /// <summary>天车共享状态（查询不触发通信）。</summary>
    private readonly ICraneStateProvider _craneStates;

    /// <summary>设备预约管理器（设备卡片黄=F 被任务预约）。</summary>
    private readonly ResourceReservationManager _reservationManager;

    /// <summary>候选任务池（TransportTaskGenerator 产物）。</summary>
    private readonly CandidateTaskPool _candidateTaskPool;

    /// <summary>工件任务总表（统计已派发任务）。</summary>
    private readonly IRuntimeTaskManager _taskManager;

    /// <summary>整轮刷新占用标记，慢通信期间跳过新节拍，避免请求在连接锁前积压。</summary>
    private int _refreshInProgress;

    /// <summary>设备卡片数据。</summary>
    public ObservableCollection<DeviceRowViewModel> Devices { get; } = new();

    /// <summary>天车卡片数据。</summary>
    public ObservableCollection<CraneRowViewModel> Cranes { get; } = new();

    /// <summary>应用服务卡片：候选任务数（TransportTaskGenerator 产物，P3 填充）。</summary>
    [ObservableProperty]
    private int _candidateTaskCount;

    /// <summary>应用服务卡片：已派发正式任务数（集中调度产物，P3 填充）。</summary>
    [ObservableProperty]
    private int _dispatchedTaskCount;

    /// <summary>候选任务悬浮详情（多行）：工件 源→目标 优先级。</summary>
    [ObservableProperty]
    private string _candidateTasksText = "无候选任务";

    /// <summary>已派发任务悬浮详情（多行）：天车 工件 源→目标 状态。</summary>
    [ObservableProperty]
    private string _dispatchedTasksText = "无已派发任务";

    /// <summary>
    /// 构造函数。订阅节拍器定期刷新。
    /// </summary>
    /// <param name="deviceRegistry">设备注册表</param>
    /// <param name="craneRegistry">天车注册表</param>
    /// <param name="craneStates">天车共享状态（查询不触发通信）</param>
    /// <param name="reservationManager">设备预约管理器</param>
    /// <param name="candidateTaskPool">候选任务池</param>
    /// <param name="taskManager">工件任务总表</param>
    /// <param name="ticker">运行态节拍器</param>
    public ScreenViewModel(
        DeviceRegistry deviceRegistry,
        CraneRegistry craneRegistry,
        ICraneStateProvider craneStates,
        ResourceReservationManager reservationManager,
        CandidateTaskPool candidateTaskPool,
        IRuntimeTaskManager taskManager,
        IRuntimeTicker ticker)
    {
        _deviceRegistry = deviceRegistry;
        _craneRegistry = craneRegistry;
        _craneStates = craneStates;
        _reservationManager = reservationManager;
        _candidateTaskPool = candidateTaskPool;
        _taskManager = taskManager;
        ticker.Tick += (_, _) => _ = RefreshAsync();
        _ = RefreshAsync();
    }

    /// <summary>尝试执行一轮刷新；上一轮未结束时跳过本次触发，不排队或补跑。</summary>
    /// <returns>本轮刷新任务；忙时立即完成。</returns>
    private async Task RefreshAsync()
    {
        // 首轮刷新与后续节拍共用入口，确保整轮最多只有一个执行者。
        //Interlocked.CompareExchange(ref location, value, comparand) 的语义是原子操作：  如果 _refreshInProgress == comparand(0)，就把它设为 value(1)，并返回原值；否则不改，直接返回原值。 正常是0 返回值是0 if哦安端位false 不return   是1说明下面的await方法还没执行完
        if (Interlocked.CompareExchange(ref _refreshInProgress, 1, 0) != 0)
        {
            return;
        }
        try
        {
            //读取天车信息
            await RefreshCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            // 即使读取或展示更新失败，也允许下一个节拍重新读取最新状态。
            Volatile.Write(ref _refreshInProgress, 0);
        }
    }

    /// <summary>
    /// 差量刷新设备与天车卡片，均读取 Application 缓存，不触发 PLC 通信。
    /// 从各个管理器（Manager/Registry）拉取最新快照，计算显示内容，然后同步到界面绑定的集合和属性上。 这个方法只是更新 ViewModel 里的数据；  1s调用1次
    /// 更新viewmodel的数据更新完 显示页面是：PropertyChanged 通知 WPF，WPF 再把新数据显示到页面上。
    /// </summary>
    private Task RefreshCoreAsync()
    {
        // 上料架的"当前工件"= 上料架队列队头（未启动/暂停不入队，无队头即无工件）
        var queueHead = _taskManager.Snapshot()
            .Where(t => t.Runtime.TaskState == WorkpieceState.Waiting
                        && t.Runtime.CurrentLocation.Kind == WorkpieceLocationKind.Queue)
            .OrderBy(t => t.Runtime.QueueOrder)
            .FirstOrDefault();

        //把界面绑定的 ObservableCollection<TItem> 集合，按数据源 source 的最新内容进行增量更新（增、删、改、排序），而不是清空重建。
        ObservableCollectionSync.SyncById(
            Devices,
            _deviceRegistry.Snapshot().OrderBy(d => d.Id).ToList(),
            row => row.Id,
            device => device.Id,
            device => new DeviceRowViewModel(device, _reservationManager.IsReserved, DisplayWorkpiece(device, queueHead)),
            (row, device) => row.Refresh(device, DisplayWorkpiece(device, queueHead)));

        // 两个页面共用后台快照；读取失败或过期时交给原卡片逻辑显示不可用。
        var craneList = _craneRegistry.Snapshot().OrderBy(c => c.No).ToList();
        var statuses = craneList.ToDictionary(c => c.Id, c => _craneStates.GetFreshStatus(c.Id));

        ObservableCollectionSync.SyncById(
            Cranes,
            craneList,
            row => row.Id,
            crane => crane.Id,
            crane => new CraneRowViewModel(crane, _deviceRegistry.Get, statuses.GetValueOrDefault(crane.Id)),
            (row, crane) => row.Refresh(crane, statuses.GetValueOrDefault(crane.Id)));

        // 顶部应用服务卡片：候选任务数、已派发任务数 + 悬浮详情
        var candidatePool = _candidateTaskPool.Snapshot();
        CandidateTaskCount = candidatePool.Count;
        CandidateTasksText = candidatePool.Count == 0
            ? "无候选任务"
            : string.Join(Environment.NewLine, candidatePool.Select(c =>
                $"{c.Workpiece.Info}  {c.Source.Name}→{c.Target.Name}  优先级{c.Priority}"));

        var dispatched = craneList
            .Where(c => c.Runtime.CurrentTask is not null)
            .Select(c =>
            {
                var t = c.Runtime.CurrentTask!;
                var wp = _taskManager.Get(t.WorkpieceRuntimeId)?.Info.ToString() ?? "?";
                var src = _deviceRegistry.Get(t.SourceDeviceId)?.Name ?? $"设备{t.SourceDeviceId}";
                var dst = _deviceRegistry.Get(t.TargetDeviceId)?.Name ?? $"设备{t.TargetDeviceId}";
                return $"{c.Name}  {wp}  {src}→{dst}  {DisplayText.CraneStateText(c.Runtime.State)}";
            })
            .ToList();
        DispatchedTaskCount = dispatched.Count;
        DispatchedTasksText = dispatched.Count == 0
            ? "无已派发任务"
            : string.Join(Environment.NewLine, dispatched);
        return Task.CompletedTask;

    }

    /// <summary>上料架显示队头工件；其余设备返回 null（用设备自身内存工件）。</summary>
    /// <param name="device">设备</param>
    /// <param name="queueHead">上料架队列队头</param>
    /// <returns>要显示的工件任务；null 表示用设备内存</returns>
    private static WorkpieceTask? DisplayWorkpiece(DeviceBase device, WorkpieceTask? queueHead)
        => device.Type == DeviceType.LoadRack ? queueHead : null;
}
