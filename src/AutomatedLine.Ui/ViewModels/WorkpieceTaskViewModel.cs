using AutomatedLine.Domain.Routing;
using AutomatedLine.Domain.Workpieces;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutomatedLine.Ui.ViewModels;

/// <summary>
/// 单条工件任务的行 ViewModel。
/// 把领域对象 <see cref="WorkpieceTask"/> 翻译成界面可读的文本，避免在 XAML 里堆枚举转换器。
/// 只读展示；数据变化时调用 <see cref="Refresh"/> 重新派生文本。
/// </summary>
public partial class WorkpieceTaskViewModel : ObservableObject
{
    /// <summary>底层工件任务（权威数据）。</summary>
    private readonly WorkpieceTask _task;

    /// <summary>板号。</summary>
    [ObservableProperty]
    private string _plateNo = string.Empty;

    /// <summary>序号。</summary>
    [ObservableProperty]
    private string _serialNo = string.Empty;

    /// <summary>直径。</summary>
    [ObservableProperty]
    private double _diameter;

    /// <summary>长度。</summary>
    [ObservableProperty]
    private double _length;

    /// <summary>当前工序文本。</summary>
    [ObservableProperty]
    private string _processText = string.Empty;

    /// <summary>当前位置文本。</summary>
    [ObservableProperty]
    private string _locationText = string.Empty;

    /// <summary>任务状态文本。</summary>
    [ObservableProperty]
    private string _stateText = string.Empty;

    /// <summary>是否允许"清除"（仅尚未派发的等待任务可清除，架构文档 §13.1）。</summary>
    [ObservableProperty]
    private bool _canClear;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="task">底层工件任务</param>
    public WorkpieceTaskViewModel(WorkpieceTask task)
    {
        _task = task;
        Refresh();
    }

    /// <summary>底层任务标识，供命令定位。</summary>
    public Guid TaskId => _task.Id;

    /// <summary>
    /// 从领域对象重新派生所有展示文本。领域状态变化后由主 ViewModel 调用。
    /// </summary>
    public void Refresh()
    {
        var info = _task.Info;
        var runtime = _task.Runtime;

        PlateNo = info.PlateNo;
        SerialNo = info.SerialNo.ToString();
        Diameter = info.Diameter;
        Length = info.Length;
        ProcessText = ProcessToText(runtime.CurrentProcess);
        LocationText = runtime.CurrentLocation.ToString();
        StateText = StateToText(runtime.TaskState);

        // 只有"等待中"的任务允许被人工清除；已派发/执行中的由调度与状态机负责
        CanClear = runtime.TaskState == WorkpieceState.Waiting;
    }

    /// <summary>
    /// 工序枚举转中文文本。
    /// </summary>
    /// <param name="process">工序</param>
    /// <returns>中文文本</returns>
    private static string ProcessToText(ProcessType process) => process switch
    {
        ProcessType.Loading => "上料",
        ProcessType.Forking => "货叉",
        ProcessType.Boring => "双头镗",
        ProcessType.Lathing => "斜床",
        ProcessType.Grinding => "研磨",
        ProcessType.Balancing => "动平衡",
        ProcessType.Marking => "打号",
        ProcessType.Checking => "验点",
        ProcessType.Unloading => "下料",
        _ => "未知"
    };

    /// <summary>
    /// 任务状态枚举转中文文本。
    /// </summary>
    /// <param name="state">任务状态</param>
    /// <returns>中文文本</returns>
    private static string StateToText(WorkpieceState state) => state switch
    {
        WorkpieceState.Waiting => "等待中",
        WorkpieceState.Assigned => "已派发",
        WorkpieceState.Running => "执行中",
        WorkpieceState.Completed => "已完成",
        WorkpieceState.Discarded => "已丢弃",
        _ => "未知"
    };
}
