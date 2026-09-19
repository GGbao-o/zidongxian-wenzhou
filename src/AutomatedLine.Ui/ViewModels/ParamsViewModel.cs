using System.Collections.ObjectModel;
using AutomatedLine.Application.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutomatedLine.Ui.ViewModels;

/// <summary>
/// 参数设置页面 ViewModel。
/// 当前只读展示 appsettings.json 的程序参数（架构文档 §13.5），编辑与保存功能后续补充。
/// </summary>
public partial class ParamsViewModel : ObservableObject
{
    /// <summary>参数项集合。</summary>
    public ObservableCollection<ParamItemViewModel> Items { get; } = new();

    /// <summary>
    /// 构造函数。把 AppOptions 展平为"分类/名称/值"的列表，便于界面统一展示。
    /// </summary>
    /// <param name="configService">配置服务</param>
    public ParamsViewModel(IConfigService configService)
    {
        var options = configService.GetAppOptions();

        void Add(string category, string name, object value)
            => Items.Add(new ParamItemViewModel(category, name, value.ToString() ?? string.Empty));

        Add("采集", "设备采集周期(ms)", options.Collection.DeviceIntervalMs);
        Add("采集", "天车采集周期(ms)", options.Collection.CraneIntervalMs);
        Add("采集", "通信超时(ms)", options.Collection.CommunicationTimeoutMs);

        Add("调度", "候选任务生成周期(ms)", options.Scheduling.GenerateIntervalMs);
        Add("调度", "调度周期(ms)", options.Scheduling.ScheduleIntervalMs);
        Add("调度", "设备预约超时(ms)", options.Scheduling.ReservationTimeoutMs);

        Add("状态机", "运动到位超时(ms)", options.StateMachine.MoveTimeoutMs);
        Add("状态机", "取料确认超时(ms)", options.StateMachine.PickTimeoutMs);
        Add("状态机", "放料确认超时(ms)", options.StateMachine.PlaceTimeoutMs);
        Add("状态机", "握手超时(ms)", options.StateMachine.HandshakeTimeoutMs);

        Add("日志", "日志目录", options.Logging.Directory);
        Add("日志", "保留天数", options.Logging.RetentionDays);
        Add("日志", "单文件上限(字节)", options.Logging.MaxFileSizeBytes);

        Add("模拟", "配置表为空时写入样例数据", options.Simulation.SeedSampleData);
    }
}

/// <summary>
/// 单个参数项。
/// </summary>
public sealed class ParamItemViewModel
{
    /// <summary>参数分类。</summary>
    public string Category { get; }

    /// <summary>参数名称。</summary>
    public string Name { get; }

    /// <summary>参数值（字符串形式展示）。</summary>
    public string Value { get; }

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="category">分类</param>
    /// <param name="name">名称</param>
    /// <param name="value">值文本</param>
    public ParamItemViewModel(string category, string name, string value)
    {
        Category = category;
        Name = name;
        Value = value;
    }
}
