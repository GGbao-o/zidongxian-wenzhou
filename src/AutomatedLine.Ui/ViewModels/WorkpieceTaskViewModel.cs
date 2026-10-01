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

    /// <summary>左堵厚。</summary>
    [ObservableProperty]
    private double _leftPlugThickness;

    /// <summary>右堵厚。</summary>
    [ObservableProperty]
    private double _rightPlugThickness;

    /// <summary>堵孔文本（70=小孔 / 100=大孔）。</summary>
    [ObservableProperty]
    private string _plugHoleText = string.Empty;

    /// <summary>打印内容。</summary>
    [ObservableProperty]
    private string _printContent = string.Empty;

    /// <summary>内孔锥度。</summary>
    [ObservableProperty]
    private double _innerHoleTaper;

    /// <summary>圆角大小。</summary>
    [ObservableProperty]
    private double _filletSize;

    /// <summary>是否做动平衡文本（需要/不需要）。</summary>
    [ObservableProperty]
    private string _needBalancingText = string.Empty;

    /// <summary>当前工序文本。</summary>
    [ObservableProperty]
    private string _processText = string.Empty;

    /// <summary>入线顺序（上料架取板顺序）；未入队显示"-"。</summary>
    [ObservableProperty]
    private string _queueOrderText = "-";

    /// <summary>下一道工序文本（由主 ViewModel 依据启用设备计算）。</summary>
    [ObservableProperty]
    private string _nextProcessText = "-";

    /// <summary>下一道工序位置（目标设备 X 位置，多个逗号分隔；由主 ViewModel 计算）。</summary>
    [ObservableProperty]
    private string _nextPositionText = "-";

    /// <summary>当前位置文本。</summary>
    [ObservableProperty]
    private string _locationText = string.Empty;

    /// <summary>任务状态文本。</summary>
    [ObservableProperty]
    private string _stateText = string.Empty;

    /// <summary>是否允许"清除/删除"（未派发的 Created/Waiting/Paused 可删，架构文档 §13.1）。</summary>
    [ObservableProperty]
    private bool _canClear;

    /// <summary>是否允许"启动"（未启动/已暂停 可启动入队）。</summary>
    [ObservableProperty]
    private bool _canStart;

    /// <summary>是否允许"暂停"（仅排队中可暂停）。</summary>
    [ObservableProperty]
    private bool _canPause;

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
        LeftPlugThickness = info.LeftPlugThickness;
        RightPlugThickness = info.RightPlugThickness;
        PlugHoleText = info.PlugHole switch
        {
            70 => "70（小孔）",
            100 => "100（大孔）",
            _ => info.PlugHole.ToString()
        };
        PrintContent = info.PrintContent;
        InnerHoleTaper = info.InnerHoleTaper;
        FilletSize = info.FilletSize;
        NeedBalancingText = info.NeedBalancing == 1 ? "需要" : "不需要";
        ProcessText = ProcessToText(runtime.CurrentProcess);
        QueueOrderText = runtime.QueueOrder > 0 ? runtime.QueueOrder.ToString() : "-";
        LocationText = runtime.CurrentLocation.ToString();
        StateText = StateToText(runtime.TaskState);

        // 行内操作可用性：未派发前（未启动/排队/暂停）才可由人工启动/暂停/删除
        CanStart = runtime.TaskState is WorkpieceState.Created or WorkpieceState.Paused;
        CanPause = runtime.TaskState == WorkpieceState.Waiting;
        CanClear = runtime.TaskState is WorkpieceState.Created or WorkpieceState.Waiting or WorkpieceState.Paused;
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
        WorkpieceState.Created => "未启动",
        WorkpieceState.Waiting => "排队中",
        WorkpieceState.Paused => "已暂停",
        WorkpieceState.Assigned => "已派发",
        WorkpieceState.Running => "执行中",
        WorkpieceState.Completed => "已完成",
        WorkpieceState.Discarded => "已丢弃",
        _ => "未知"
    };
}
