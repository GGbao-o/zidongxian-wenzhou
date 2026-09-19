using System.Collections.ObjectModel;
using AutomatedLine.Application.Alarms;
using AutomatedLine.Domain.Alarms;
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

    /// <summary>报警行集合。</summary>
    public ObservableCollection<AlarmRowViewModel> Alarms { get; } = new();

    /// <summary>当前选中的报警。</summary>
    [ObservableProperty]
    private AlarmRowViewModel? _selectedAlarm;

    /// <summary>操作提示。</summary>
    [ObservableProperty]
    private string _message = "报警只存内存并同步写磁盘日志。异常由程序自动清理，无需人工干预。";

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="alarmStore">活动报警存储</param>
    public AlarmViewModel(ActiveAlarmStore alarmStore)
    {
        _alarmStore = alarmStore;
        _alarmStore.Changed += (_, _) => RefreshOnUiThread();
        Refresh();
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
        Alarms.Clear();
        foreach (var alarm in _alarmStore.Snapshot())
        {
            Alarms.Add(new AlarmRowViewModel(alarm));
        }
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
