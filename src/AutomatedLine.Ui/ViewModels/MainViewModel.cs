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
    public MainViewModel(
        IConfigService configService,
        IRuntimeTaskManager taskManager,
        DeviceRegistry deviceRegistry,
        CraneRegistry craneRegistry,
        IDialogService dialogService,
        IColdStartChecker coldStartChecker,
        LineControlService lineControl)
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

        // 启动前先做冷启动核对：现场有料但内存无数据 → 仅告警，不阻塞（天车状态按需读取）
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
        var snapshot = _taskManager.Snapshot();

        // 入线顺序 = 上料架待取板队列里的**排位**（1、2、3…连续）。
        // 只有"已启动、排队中、在队列"的任务占位；未启动/暂停不占位，后面的自动补位。
        // 顺序依据入队时分配的入队序号（QueueOrder）：启动入队尾、暂停出队、再启动重新入队尾。
        var queuePositions = new Dictionary<Guid, int>();
        var rank = 0;
        foreach (var t in snapshot
                     .Where(t => t.Runtime.TaskState == WorkpieceState.Waiting
                                 && t.Runtime.CurrentLocation.Kind == WorkpieceLocationKind.Queue)
                     .OrderBy(t => t.Runtime.QueueOrder))
        {
            queuePositions[t.Id] = ++rank;
        }

        Tasks.Clear();
        foreach (var task in snapshot)
        {
            var row = new WorkpieceTaskViewModel(task)
            {
                QueueOrderText = queuePositions.TryGetValue(task.Id, out var pos) ? pos.ToString() : "-"
            };
            FillNextProcess(row, task, devices);
            Tasks.Add(row);
        }

        TaskCount = Tasks.Count;
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
        var next = Route.GetNextAvailable(current, type => devices.Any(d => d.Type == type && d.IsEnabled));
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
