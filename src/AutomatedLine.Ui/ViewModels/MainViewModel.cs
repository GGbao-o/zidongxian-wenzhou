using AutomatedLine.Application.Abstractions;
using AutomatedLine.Domain.Line;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutomatedLine.Ui.ViewModels;

/// <summary>
/// 主页面 ViewModel。
/// P0 阶段只负责展示线路状态与基础信息，并把"启动/暂停"作为命令骨架占位；
/// 后续 P1/P3 再接入 RuntimeTaskManager、候选任务池与调度器。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    /// <summary>配置服务。P0 仅用于读取程序参数做展示，后续业务服务也走构造函数注入。</summary>
    private readonly IConfigService _configService;

    /// <summary>线路状态。绑定到底层状态标识，用于大屏/主页面颜色映射。</summary>
    [ObservableProperty]
    private LineState _lineState = LineState.Stopped;

    /// <summary>线路状态的中文文本，供界面直接显示（避免在 XAML 里堆枚举转换器）。</summary>
    [ObservableProperty]
    private string _lineStateText = "未启动";

    /// <summary>候选任务生成周期（毫秒），从配置读取，用于验证配置链路是否打通。</summary>
    [ObservableProperty]
    private int _generateIntervalMs;

    /// <summary>
    /// 构造函数。通过 DI 注入配置服务，ViewModel 不直接读 JSON 文件。
    /// </summary>
    /// <param name="configService">配置服务</param>
    public MainViewModel(IConfigService configService)
    {
        _configService = configService;

        // 读取调度周期，验证 appsettings.json → ConfigService → ViewModel 的链路可用
        var options = _configService.GetAppOptions();
        GenerateIntervalMs = options.Scheduling.GenerateIntervalMs;

        Console.WriteLine($"[MainViewModel] 初始化完成，候选任务生成周期={GenerateIntervalMs}ms");
    }

    /// <summary>
    /// 启动线路命令（P0 仅更新界面状态，P3 接入 LineControlService）。
    /// </summary>
    [RelayCommand]
    private void StartLine()
    {
        // P0 是骨架：只切换状态文本，后续替换为调用 LineControlService.StartLine
        LineState = LineState.Running;
        LineStateText = "自动运行";
        Console.WriteLine("[MainViewModel] 启动线路（骨架占位）");
    }

    /// <summary>
    /// 暂停线路命令（P0 仅更新界面状态，后续接入 LineControlService.PauseLine）。
    /// </summary>
    [RelayCommand]
    private void PauseLine()
    {
        LineState = LineState.Paused;
        LineStateText = "已暂停";
        Console.WriteLine("[MainViewModel] 暂停线路（骨架占位）");
    }

    /// <summary>
    /// 线路状态变更时同步中文文本，保证界面显示与状态一致。
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
    }
}
