using AutomatedLine.Domain.Cranes;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutomatedLine.Ui.ViewModels;

/// <summary>
/// 天车列表/大屏中的一行。把天车对象与信号快照翻译成界面文本。
/// </summary>
public partial class CraneRowViewModel : ObservableObject
{
    /// <summary>天车号。</summary>
    public int Id { get; }

    /// <summary>序号。</summary>
    [ObservableProperty]
    private int _no;

    /// <summary>名称。</summary>
    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>通信状态文本。</summary>
    [ObservableProperty]
    private string _connectionText = "未连接";

    /// <summary>X 坐标。</summary>
    [ObservableProperty]
    private double _x;

    /// <summary>Y 坐标。</summary>
    [ObservableProperty]
    private double _y;

    /// <summary>Z 坐标。</summary>
    [ObservableProperty]
    private double _z;

    /// <summary>状态文本。</summary>
    [ObservableProperty]
    private string _stateText = string.Empty;

    /// <summary>当前任务文本。</summary>
    [ObservableProperty]
    private string _taskText = "无";

    /// <summary>报警文本。</summary>
    [ObservableProperty]
    private string _alarmText = "正常";

    /// <summary>关键信号摘要（供大屏紧凑展示）。</summary>
    [ObservableProperty]
    private string _signalSummary = string.Empty;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="crane">天车对象</param>
    public CraneRowViewModel(Crane crane)
    {
        Id = crane.Id;
        Refresh(crane);
    }

    /// <summary>
    /// 依据天车最新状态刷新展示字段。
    /// </summary>
    /// <param name="crane">天车对象</param>
    public void Refresh(Crane crane)
    {
        No = crane.No;
        Name = crane.Name;
        StateText = StateToText(crane.Runtime.State);

        var signals = crane.Signals;
        ConnectionText = crane.IsConnected ? "已连接" : "未连接";
        X = signals?.GetDouble("X") ?? 0;
        Y = signals?.GetDouble("Y") ?? 0;
        Z = signals?.GetDouble("Z") ?? 0;
        AlarmText = signals?.GetBool("Alarm") == true ? "报警" : "正常";
        TaskText = crane.Runtime.CurrentTask is null ? "无" : crane.Runtime.CurrentTask.TaskId.ToString("N")[..8];

        SignalSummary = signals is null
            ? "无数据"
            : $"到位={(signals.GetBool("InPosition") ? "是" : "否")} 有料={(signals.GetBool("HasMaterial") ? "是" : "否")} 伴信号={signals.GetInt("CompanionSignal")}";
    }

    /// <summary>
    /// 天车状态转中文文本。
    /// </summary>
    /// <param name="state">天车状态</param>
    /// <returns>中文文本</returns>
    private static string StateToText(CraneState state) => state switch
    {
        CraneState.Idle => "空闲",
        CraneState.Reserved => "已分配",
        CraneState.Running => "执行中",
        CraneState.WaitingDevice => "等待设备",
        CraneState.Paused => "暂停",
        CraneState.Faulted => "故障",
        CraneState.Unavailable => "不可用",
        _ => "未知"
    };
}
