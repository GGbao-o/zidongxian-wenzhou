using System.Collections.ObjectModel;
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Alarms;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Alarms;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Ui.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutomatedLine.Ui.ViewModels;

/// <summary>
/// 报警处理页面 ViewModel。
/// 活动报警来自内存 ActiveAlarmStore，明细由 AlarmManager 同步写磁盘（alarm.txt）。
/// 提供受限的人工处理按钮：清除物理阻塞（实现）、释放逻辑锁/补内存（占位，P6 实现）。
/// </summary>
public partial class AlarmViewModel : ObservableObject
{
    /// <summary>活动报警存储。</summary>
    private readonly ActiveAlarmStore _alarmStore;

    /// <summary>设备注册表（清除物理阻塞用）。</summary>
    private readonly DeviceRegistry _deviceRegistry;

    /// <summary>天车注册表（清除物理阻塞用）。</summary>
    private readonly CraneRegistry _craneRegistry;

    /// <summary>日志。</summary>
    private readonly IAppLogger _logger;

    /// <summary>报警行集合。</summary>
    public ObservableCollection<AlarmRowViewModel> Alarms { get; } = new();

    /// <summary>当前选中的报警。</summary>
    [ObservableProperty]
    private AlarmRowViewModel? _selectedAlarm;

    /// <summary>操作提示。</summary>
    [ObservableProperty]
    private string _message = "报警只存内存并同步写磁盘日志；可对选中报警做人工处理。";

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="alarmStore">活动报警存储</param>
    /// <param name="deviceRegistry">设备注册表</param>
    /// <param name="craneRegistry">天车注册表</param>
    /// <param name="logger">日志</param>
    public AlarmViewModel(
        ActiveAlarmStore alarmStore,
        DeviceRegistry deviceRegistry,
        CraneRegistry craneRegistry,
        IAppLogger logger)
    {
        _alarmStore = alarmStore;
        _deviceRegistry = deviceRegistry;
        _craneRegistry = craneRegistry;
        _logger = logger;

        _alarmStore.Changed += (_, _) => RefreshOnUiThread();
        Refresh();
    }

    /// <summary>清除全部活动报警。</summary>
    [RelayCommand]
    private void ClearAll()
    {
        _alarmStore.Clear();
        Message = "已清除全部活动报警（磁盘日志保留）。";
    }

    /// <summary>
    /// 清除选中报警所关联资源的物理阻塞。
    /// </summary>
    [RelayCommand]
    private void ClearPhysicalBlock()
    {
        var alarm = SelectedAlarm?.Alarm;
        if (alarm?.ResourceId is null)
        {
            Message = "请选择一条关联了具体资源的报警。";
            return;
        }

        if (alarm.ResourceIsCrane)
        {
            var crane = _craneRegistry.Get(alarm.ResourceId.Value);
            if (crane is not null)
            {
                crane.Runtime.State = Domain.Cranes.CraneState.Idle;
            }
        }
        else
        {
            var device = _deviceRegistry.Get(alarm.ResourceId.Value);
            if (device is not null)
            {
                device.ResourceState = ResourceState.Available;
            }
        }

        _alarmStore.Remove(alarm.Id);
        _logger.Info(Domain.Diagnostics.LogCategory.Operation,
            $"人工清除物理阻塞：{(alarm.ResourceIsCrane ? "天车" : "设备")}{alarm.ResourceId}");
        Message = "已清除该资源的物理阻塞。";
    }

    /// <summary>释放逻辑锁（占位，随 P6 异常处理实现）。</summary>
    [RelayCommand]
    private void ReleaseLocks()
    {
        _logger.Warn(Domain.Diagnostics.LogCategory.Operation, "人工释放逻辑锁：功能待 P6 实现");
        Message = "释放逻辑锁功能将在异常处理阶段实现。";
    }

    /// <summary>补内存（占位，随 P6 异常处理实现）。</summary>
    [RelayCommand]
    private void Replenish()
    {
        _logger.Warn(Domain.Diagnostics.LogCategory.Operation, "人工补内存：功能待 P6 实现");
        Message = "补内存功能将在异常处理阶段实现。";
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

    /// <summary>发生时间文本。</summary>
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
