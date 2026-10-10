using System.Collections.ObjectModel;
using System.Windows.Media;
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Line;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Domain.Line;
using AutomatedLine.Domain.Routing;
using AutomatedLine.Domain.Workpieces;
using AutomatedLine.Ui.Helpers;
using AutomatedLine.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutomatedLine.Ui.ViewModels;

/// <summary>
/// 主页面 ViewModel。
/// P1 负责：展示线路状态、从内存任务表读取并展示工件任务、通过弹窗增删任务。
/// 概览卡片中的设备/天车数量来自运行态注册表，用于验证 P2 配置装载结果。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    /// <summary>配置服务。用于读取程序参数并在界面展示，验证配置链路。</summary>
    private readonly IConfigService _configService;

    /// <summary>工件任务内存总表。任务增删都经由它，保证唯一事实来源。</summary>
    private readonly IRuntimeTaskManager _taskManager;

    /// <summary>设备注册表。用于展示已装载设备数量。</summary>
    private readonly DeviceRegistry _deviceRegistry;

    /// <summary>天车注册表。用于展示已装载天车数量。</summary>
    private readonly CraneRegistry _craneRegistry;

    /// <summary>弹窗服务。用于打开"添加工件"对话框。</summary>
    private readonly IDialogService _dialogService;

    /// <summary>冷启动核对。启动线路时检查现场是否有无法关联的内存在制品。</summary>
    private readonly IColdStartChecker _coldStartChecker;

    /// <summary>线路控制服务。启停与状态以它为准。</summary>
    private readonly LineControlService _lineControl;

    /// <summary>线路状态（枚举）。</summary>
    [ObservableProperty]
    private LineState _lineState = LineState.Stopped;

    /// <summary>线路状态中文文本。</summary>
    [ObservableProperty]
    private string _lineStateText = "未启动";

    /// <summary>线路状态对应的强调色，浅色风格下用颜色区分状态。</summary>
    [ObservableProperty]
    private Brush _lineStateBrush = Brushes.Gray;

    /// <summary>入线按钮文本：未运行显示"启动线路"，运行中显示"暂停线路"。</summary>
    [ObservableProperty]
    private string _lineButtonText = "启动线路";

    /// <summary>候选任务生成周期（毫秒）。</summary>
    [ObservableProperty]
    private int _generateIntervalMs;

    /// <summary>当前工件任务数量。</summary>
    [ObservableProperty]
    private int _taskCount;

    /// <summary>已装载设备数量。</summary>
    [ObservableProperty]
    private int _deviceCount;

    /// <summary>已装载天车数量。</summary>
    [ObservableProperty]
    private int _craneCount;

    /// <summary>当前工件任务列表（界面绑定源）。</summary>
    public ObservableCollection<WorkpieceTaskViewModel> Tasks { get; } = new();

    /// <summary>
    /// 构造函数。注入配置、任务表、注册表与弹窗服务。
    /// </summary>
    /// <param name="configService">配置服务</param>
    /// <param name="taskManager">内存任务表</param>
    /// <param name="deviceRegistry">设备注册表</param>
    /// <param name="craneRegistry">天车注册表</param>
    /// <param name="dialogService">弹窗服务</param>
    /// <param name="coldStartChecker">冷启动核对</param>
    /// <param name="lineControl">线路控制服务</param>
    /// <param name="ticker">界面节拍器（每 1s），用于就地刷新任务行进度</param>
    public MainViewModel(
        IConfigService configService,
        IRuntimeTaskManager taskManager,
        DeviceRegistry deviceRegistry,
        CraneRegistry craneRegistry,
        IDialogService dialogService,
        IColdStartChecker coldStartChecker,
        LineControlService lineControl,
        IRuntimeTicker ticker)
    {
        _configService = configService;
        _taskManager = taskManager;
        _deviceRegistry = deviceRegistry;
        _craneRegistry = craneRegistry;
        _dialogService = dialogService;
        _coldStartChecker = coldStartChecker;
        _lineControl = lineControl;

        // 任务表变化时刷新界面列表；事件可能在非 UI 线程触发，需切回 UI 线程
        _taskManager.Changed += OnTasksChanged;

        // 线路状态以 LineControlService 为准，状态变化时刷新界面
        _lineControl.StateChanged += (_, _) => RefreshLineState();
        RefreshLineState();

        // 任务列表刷新分两条路（架构文档 §13.1、《页面布局/主页面.md》§6）：
        //  - 结构变化（增删任务、启动/暂停入队）→ _taskManager.Changed → 全量重建列表；
        //  - 进度变化（状态/位置/工序）由应用层直接改领域对象、不发事件 → 用 1s 节拍就地刷新行文本。
        // 就地刷新不重建集合，避免选中行与滚动位置每秒被清掉。
        ticker.Tick += (_, _) => RefreshRowStates();

        GenerateIntervalMs = _configService.GetAppOptions().Scheduling.GenerateIntervalMs;

        // 初始同步一次，保证界面打开时已有数据
        RefreshTasks();
        RefreshRegistries();

        Console.WriteLine($"[MainViewModel] 初始化完成，候选任务生成周期={GenerateIntervalMs}ms");
    }

    /// <summary>
    /// 添加任务命令：弹窗收集工件数据，确认后加入内存任务表。
    /// </summary>
    [RelayCommand]
    private void AddTask()
    {
        var info = _dialogService.ShowAddWorkpieceDialog();
        if (info is null)
        {
            // 用户取消，不做任何事
            return;
        }

        _taskManager.Add(info);
        Console.WriteLine($"[MainViewModel] 已添加任务 {info}");
    }

    /// <summary>
    /// 启动任务命令：把未启动/已暂停的任务置为排队中（入队，参与候选生成）。
    /// </summary>
    /// <param name="row">任务行</param>
    [RelayCommand]
    private void StartTask(WorkpieceTaskViewModel? row)
    {
        if (row is null || !row.CanStart)
        {
            return;
        }

        if (_taskManager.Start(row.TaskId))
        {
            Console.WriteLine($"[MainViewModel] 已启动任务 {row.PlateNo}/{row.SerialNo}");
        }
    }

    /// <summary>
    /// 暂停任务命令：把排队中的任务置为已暂停（该任务不再生成候选；线路级暂停是另一回事）。
    /// </summary>
    /// <param name="row">任务行</param>
    [RelayCommand]
    private void PauseTask(WorkpieceTaskViewModel? row)
    {
        if (row is null || !row.CanPause)
        {
            return;
        }

        if (_taskManager.Pause(row.TaskId))
        {
            Console.WriteLine($"[MainViewModel] 已暂停任务 {row.PlateNo}/{row.SerialNo}");
        }
    }

    /// <summary>
    /// 清除任务命令：清除未启动/排队/暂停/已丢弃/已完成的任务（架构文档 §13.1）。
    /// 删除前先清掉设备/天车对该工件的引用，避免悬挂引用把设备占死。
    /// </summary>
    /// <param name="row">待清除的任务行</param>
    [RelayCommand]
    private void ClearTask(WorkpieceTaskViewModel? row)
    {
        if (row is null || !row.CanClear)
        {
            return;
        }

        _deviceRegistry.ClearWorkpiece(row.TaskId);
        _craneRegistry.ClearWorkpiece(row.TaskId);
        _taskManager.Remove(row.TaskId);
        Console.WriteLine($"[MainViewModel] 已清除任务 {row.PlateNo}/{row.SerialNo}");
    }

    /// <summary>
    /// 编辑任务命令：修改**未入线**任务的工件数据
    /// （长度 / 直径 / 左堵厚 / 右堵厚 / 堵孔 / 打印内容 / 内孔锥度 / 圆角大小 / 斜床工艺）。
    /// 版号、序号属于身份字段，编辑模式下只读。
    /// 入线后一律不允许编辑：工件可能已被取走、或已按旧参数加工过。
    /// </summary>
    /// <param name="row">待编辑的任务行</param>
    [RelayCommand]
    private void EditTask(WorkpieceTaskViewModel? row)
    {
        if (row is null || !row.CanEdit)
        {
            return;
        }

        var task = _taskManager.Get(row.TaskId);
        if (task is null)
        {
            return;
        }

        // 弹窗用当前数据预填；确定后返回新的工件数据（RuntimeId 沿用，任务身份不变）
        var updated = _dialogService.ShowEditWorkpieceDialog(task.Info);
        if (updated is null)
        {
            return;
        }

        // 【双保险】服务端再判一次状态：弹窗打开期间任务可能已被启动，此时必须拒绝
        if (_taskManager.TryUpdate(row.TaskId, updated))
        {
            Console.WriteLine($"[MainViewModel] 已编辑任务 {updated.PlateNo}/{updated.SerialNo}");
        }
        else
        {
            _dialogService.ShowMessage(
                "该任务已入线（已启动 / 已派发 / 执行中），不允许再修改工件数据。",
                "无法编辑");
        }
    }

    /// <summary>
    /// 入线按钮（启动/暂停共用一个按钮）：
    /// 未运行 → 先冷启动核对再启动线路（生成器等服务随之工作）；运行中 → 暂停线路（服务停止生成）。
    /// </summary>
    [RelayCommand]
    private async Task ToggleLine()
    {
        if (_lineControl.IsRunning)
        {
            _lineControl.Pause();
            return;
        }

        // 启动前先核对有效采集快照；现场有料但内存无数据仍只告警，保持原启动策略。
        var blocks = await _coldStartChecker.CheckAsync();
        if (blocks.Count > 0)
        {
            Console.WriteLine($"[MainViewModel] 冷启动核对发现 {blocks.Count} 个现场残留资源");
        }

        _lineControl.Start();
    }

    /// <summary>
    /// 从 LineControlService 同步线路状态到界面。
    /// </summary>
    private void RefreshLineState()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => LineState = _lineControl.State);
            return;
        }

        LineState = _lineControl.State;
    }

    /// <summary>
    /// 任务表变化回调。切回 UI 线程后重建列表。
    /// </summary>
    private void OnTasksChanged(object? sender, EventArgs e)
    {
        // 注意用全限定名：AutomatedLine 下存在 Application 命名空间，直接写 Application 会被解析成命名空间
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            // 非 UI 线程触发：异步切回 UI 线程刷新
            dispatcher.Invoke(RefreshTasks);
            return;
        }

        RefreshTasks();
    }

    /// <summary>
    /// 用内存任务表的快照重建界面列表。
    /// 数据量小，直接全量重建；后续若任务数增大再改为差量更新。
    /// </summary>
    private void RefreshTasks()
    {
        var devices = _deviceRegistry.Snapshot();
        var snapshot = _taskManager.Snapshot();  // 列表是新的，但元素是【同一批 WorkpieceTask 实例】  拿到的是和 Application 层共用的同一批 WorkpieceTask 对象

        // 入线顺序 = 上料架待取板队列里的**排位**（1、2、3…连续），算法见 BuildQueuePositions
        var queuePositions = BuildQueuePositions(snapshot);

        Tasks.Clear();
        //便利任务缓存 进行一个个添加到列表中 页面就会变
        foreach (var task in snapshot)
        {
            var row = new WorkpieceTaskViewModel(task)  // 行 VM 内部 _task = task ← 引用，不是拷贝
            {
                QueueOrderText = queuePositions.TryGetValue(task.Id, out var pos) ? pos.ToString() : "-"
            };
            FillNextProcess(row, task, devices);
            Tasks.Add(row);
        }

        TaskCount = Tasks.Count;
    }

    /// <summary>
    /// 就地刷新任务行的**进度类**文本（状态 / 当前位置 / 当前工序 / 入线顺序 / 下一道工序）。
    /// 由界面节拍器每秒在 UI 线程调用；不重建集合，因此不会丢选中行与滚动位置。
    /// 行数或任务标识发生变化时退化为全量重建（增删本就由 <see cref="_taskManager"/> 的事件驱动）。
    /// </summary>
    private void RefreshRowStates()
    {
        var devices = _deviceRegistry.Snapshot();
        var snapshot = _taskManager.Snapshot();

        // 数量变了：说明有增删，交给全量重建（与行内按钮可用性保持一致）
        if (snapshot.Count != Tasks.Count)
        {
            RefreshTasks();
            RefreshRegistries();
            return;
        }

        var rows = new Dictionary<Guid, WorkpieceTaskViewModel>(Tasks.Count);
        foreach (var row in Tasks)
        {
            rows[row.TaskId] = row;
        }

        var queuePositions = BuildQueuePositions(snapshot);

        foreach (var task in snapshot)
        {
            if (!rows.TryGetValue(task.Id, out var row))
            {
                // 数量相同但标识对不上（集合被整体替换过）：同样退化为全量重建
                RefreshTasks();
                RefreshRegistries();
                return;
            }

            // 行 VM 从领域对象重新派生状态/位置/工序等文本；
            // 值未变化时 ObservableObject 不会触发通知，所以每秒调用没有额外界面开销
            row.Refresh();
            row.QueueOrderText = queuePositions.TryGetValue(task.Id, out var pos) ? pos.ToString() : "-";
            FillNextProcess(row, task, devices);
        }

        // 概览卡的设备/天车数量随节拍一并刷新，配置重载后无需外部再调用 RefreshRegistries
        RefreshRegistries();
    }

    /// <summary>
    /// 计算入线顺序：上料架待取板队列里的**连续排位**（1、2、3…）。
    /// 只有"已启动、排队中、在队列"的任务占位；未启动/暂停不占位，后面的自动补位。
    /// 顺序依据入队时分配的 <c>QueueOrder</c>：启动入队尾、暂停出队、再启动重新入队尾。
    /// </summary>
    /// <param name="snapshot">任务快照</param>
    /// <returns>任务标识 → 排位（从 1 起）</returns>
    private static Dictionary<Guid, int> BuildQueuePositions(IReadOnlyList<WorkpieceTask> snapshot)
    {
        var queuePositions = new Dictionary<Guid, int>();
        var rank = 0;
        foreach (var t in snapshot
                     .Where(t => t.Runtime.TaskState == WorkpieceState.Waiting
                                 && t.Runtime.CurrentLocation.Kind == WorkpieceLocationKind.Queue)
                     .OrderBy(t => t.Runtime.QueueOrder))
        {
            queuePositions[t.Id] = ++rank;
        }

        return queuePositions;
    }

    /// <summary>工艺路线（只读，用于展示"下一道工序/位置"）。</summary>
    private static readonly ProcessRoute Route = ProcessRoute.CreateDefault();

    /// <summary>
    /// 依据启用设备计算并填充"下一道工序 + 其目标设备位置"。
    /// 队列中的新工件当前工序为 Unknown，等价"上料"，从它往后找第一道有启用设备的工序。
    /// </summary>
    /// <param name="row">行 VM</param>
    /// <param name="task">工件任务</param>
    /// <param name="devices">设备快照</param>
    private static void FillNextProcess(WorkpieceTaskViewModel row, WorkpieceTask task, IReadOnlyList<DeviceBase> devices)
    {
        var current = task.Runtime.CurrentProcess == ProcessType.Unknown ? ProcessType.Loading : task.Runtime.CurrentProcess;
        var next = Route.GetNextAvailable(current, ProcessRoute.AvailableDeviceFilter(devices));
        if (next is null)
        {
            row.NextProcessText = "已完成";
            row.NextPositionText = "-";
            return;
        }

        row.NextProcessText = DisplayText.ProcessText(next.Process);
        var positions = devices
            .Where(d => d.Type == next.DeviceType && d.IsEnabled)
            .Select(d => d.PositionX.ToString("0.###"))
            .ToList();
        row.NextPositionText = positions.Count > 0 ? string.Join(", ", positions) : "-";
    }

    /// <summary>
    /// 刷新概览卡片中的设备/天车数量。配置装载后由外部再次调用即可更新。
    /// </summary>
    public void RefreshRegistries()
    {
        DeviceCount = _deviceRegistry.Snapshot().Count;
        CraneCount = _craneRegistry.Snapshot().Count;
    }

    /// <summary>
    /// 线路状态变更时同步中文文本与状态色。
    /// </summary>
    /// <param name="value">新的线路状态</param>
    partial void OnLineStateChanged(LineState value)
    {
        LineStateText = value switch
        {
            LineState.Stopped => "未启动",
            LineState.Starting => "启动中",
            LineState.Running => "自动运行",
            LineState.PauseRequested => "请求暂停",
            LineState.Paused => "已暂停",
            LineState.PausedByFault => "故障暂停",
            _ => "未知"
        };

        // 启动/暂停共用一个按钮：运行/启动中显示"暂停线路"，其余显示"启动线路"
        LineButtonText = value is LineState.Running or LineState.Starting ? "暂停线路" : "启动线路";

        // 颜色与状态语义一致：绿=正常，蓝=运行，橙=等待，红=故障，灰=停止
        LineStateBrush = value switch
        {
            LineState.Running => new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)),
            LineState.Starting => new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)),
            LineState.Paused => new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06)),
            LineState.PauseRequested => new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06)),
            LineState.PausedByFault => new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)),
            LineState.Stopped => new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
            _ => new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B))
        };
    }
}
