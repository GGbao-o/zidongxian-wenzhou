using System.Collections.ObjectModel;
using AutomatedLine.Application.Alarms;
using AutomatedLine.Domain.Alarms;
using AutomatedLine.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutomatedLine.Ui.ViewModels;

/// <summary>
/// 报警处理页面 ViewModel。
/// 活动报警来自内存 ActiveAlarmStore；明细由 AlarmManager 同步写磁盘（alarm.txt）。
/// 按架构文档 §10.4：异常由程序自动清理，**不设"人工清内存/释放锁/补内存"按钮**，
/// 页面只做报警查看与"清除报警列表"。
/// </summary>
public partial class AlarmViewModel : ObservableObject
{
    /// <summary>活动报警存储。</summary>
    private readonly ActiveAlarmStore _alarmStore;
    private readonly IDialogService _dialogs;

    /// <summary>报警行集合。</summary>
    public ObservableCollection<AlarmRowViewModel> Alarms { get; } = new();

    /// <summary>当前选中的报警。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ViewDetailsCommand))]
    private AlarmRowViewModel? _selectedAlarm;

    /// <summary>操作提示。</summary>
    [ObservableProperty]
    private string _message = "选中后查看详情，或双击报警；详情只展示故障时信息，不执行设备和线路操作。";

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="alarmStore">活动报警存储</param>
    /// <param name="dialogs">统一只读详情窗口。</param>
    public AlarmViewModel(ActiveAlarmStore alarmStore, IDialogService dialogs)
    {
        _alarmStore = alarmStore;
        _dialogs = dialogs;
        _alarmStore.Changed += (_, _) => RefreshOnUiThread();
        Refresh();
    }

    private bool CanViewDetails => SelectedAlarm is not null;

    /// <summary>展示选中报警的固定快照，不读取当前任务或日志。</summary>
    [RelayCommand(CanExecute = nameof(CanViewDetails))]
    private void ViewDetails()
    {
        if (SelectedAlarm is { } row) _dialogs.ShowAlarmDetails(row.Alarm);
    }

    /// <summary>清除报警列表（仅清内存，磁盘日志保留）。</summary>
    [RelayCommand]
    private void ClearAll()
    {
        _alarmStore.Clear();
        Message = "已清除报警列表（磁盘日志保留）。";
    }

    /// <summary>在 UI 线程刷新报警列表。</summary>
    private void RefreshOnUiThread()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(Refresh);
            return;
        }

        Refresh();
    }

    /// <summary>重建报警列表。</summary>
    private void Refresh()
    {
        // 列表重建后按ID恢复选择，重复报警合并不让操作员丢失正在选中的行。
        var selectedId = SelectedAlarm?.Alarm.Id;
        Alarms.Clear();
        foreach (var alarm in _alarmStore.Snapshot())
        {
            Alarms.Add(new AlarmRowViewModel(alarm));
        }
        SelectedAlarm = Alarms.FirstOrDefault(row => row.Alarm.Id == selectedId);
    }
}

/// <summary>
/// 报警行。
/// </summary>
public sealed class AlarmRowViewModel
{
    /// <summary>底层报警对象。</summary>
    public ActiveAlarm Alarm { get; }

    /// <summary>最近发生时间文本。</summary>
    public string TimeText => Alarm.LastAt.ToString("MM-dd HH:mm:ss");

    /// <summary>级别文本。</summary>
    public string LevelText => Alarm.Level switch
    {
        AlarmLevel.Info => "提示",
        AlarmLevel.Warning => "警告",
        AlarmLevel.Error => "错误",
        AlarmLevel.Critical => "严重",
        _ => "未知"
    };

    /// <summary>来源。</summary>
    public string Source => Alarm.Source;

    /// <summary>内容。</summary>
    public string Message => Alarm.Message;

    /// <summary>列表只显示单行摘要，详情和复制仍使用未截断的完整正文。</summary>
    public string Summary
    {
        get
        {
            var text = Alarm.Message.Replace("\r", " ").Replace("\n", " ");
            return text.Length <= 140 ? text : text[..140] + "……";
        }
    }

    /// <summary>系统动作。</summary>
    public string SystemAction => Alarm.SystemAction ?? "-";

    /// <summary>重复次数。</summary>
    public int Count => Alarm.Count;

    /// <summary>关联资源文本。</summary>
    public string ResourceText => Alarm.ResourceId is null
        ? "-"
        : $"{(Alarm.ResourceIsCrane ? "天车" : "设备")}{Alarm.ResourceId}";

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="alarm">报警对象</param>
    public AlarmRowViewModel(ActiveAlarm alarm)
    {
        Alarm = alarm;
    }
}
