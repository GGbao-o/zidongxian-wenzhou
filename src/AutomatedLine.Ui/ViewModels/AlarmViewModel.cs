using CommunityToolkit.Mvvm.ComponentModel;

namespace AutomatedLine.Ui.ViewModels;

/// <summary>
/// 报警处理页面 ViewModel（占位）。
/// 活动报警应读内存 ActiveAlarmStore；报警明细同时按天落盘到 Logs/yyyy-MM-dd/alarm.txt。
/// 人工异常处理按钮（补内存、释放逻辑锁、清除物理阻塞）后续随 P6 补充。
/// </summary>
public partial class AlarmViewModel : ObservableObject
{
    /// <summary>页面说明文本，向操作员交代当前状态与后续功能。</summary>
    public string Description { get; } =
        "当前无活动报警。报警只存内存并同步写入磁盘日志（Logs/日期/alarm.txt）；" +
        "任务丢弃记录与人工恢复按钮将在异常处理阶段补充。";
}
