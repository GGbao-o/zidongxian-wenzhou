using System.Collections.ObjectModel;
using System.Windows.Media;
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Line;
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

    /// <summary>线路状态（枚举）。</summary>
    [ObservableProperty]
    private LineState _lineState = LineState.Stopped;

    /// <summary>线路状态中文文本。</summary>
    [ObservableProperty]
    private string _lineStateText = "未启动";

    /// <summary>线路状态对应的强调色，浅色风格下用颜色区分状态。</summary>
    [ObservableProperty]
    private Brush _lineStateBrush = Brushes.Gray;

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
    public MainViewModel(
        IConfigService configService,
        IRuntimeTaskManager taskManager,
        DeviceRegistry deviceRegistry,
        CraneRegistry craneRegistry,
        IDialogService dialogService)
    {
        _configService = configService;
        _taskManager = taskManager;
        _deviceRegistry = deviceRegistry;
        _craneRegistry = craneRegistry;
        _dialogService = dialogService;

        // 任务表变化时刷新界面列表；事件可能在非 UI 线程触发，需切回 UI 线程
        _taskManager.Changed += OnTasksChanged;

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
    /// 清除任务命令：仅允许清除尚未派发的等待任务（架构文档 §13.1）。
    /// </summary>
    /// <param name="row">待清除的任务行</param>
    [RelayCommand]
    private void ClearTask(WorkpieceTaskViewModel? row)
    {
        if (row is null || !row.CanClear)
        {
            return;
        }

        _taskManager.Remove(row.TaskId);
        Console.WriteLine($"[MainViewModel] 已清除任务 {row.PlateNo}/{row.SerialNo}");
    }

    /// <summary>
    /// 启动线路命令（P2 仍为骨架，P3 接入 LineControlService）。
    /// </summary>
    [RelayCommand]
    private void StartLine()
    {
        LineState = LineState.Running;
        Console.WriteLine("[MainViewModel] 启动线路（骨架占位）");
    }

    /// <summary>
    /// 暂停线路命令（P2 仍为骨架，后续接入 LineControlService）。
    /// </summary>
    [RelayCommand]
    private void PauseLine()
    {
        LineState = LineState.Paused;
        Console.WriteLine("[MainViewModel] 暂停线路（骨架占位）");
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
        Tasks.Clear();
        foreach (var task in _taskManager.Snapshot())
        {
            Tasks.Add(new WorkpieceTaskViewModel(task));
        }

        TaskCount = Tasks.Count;
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
