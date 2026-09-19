using AutomatedLine.Application.Configuration;
using AutomatedLine.Domain.Cranes;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Domain.Signals;
using AutomatedLine.Ui.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutomatedLine.Ui.ViewModels;

/// <summary>
/// 天车管理页的一行：**数据库配置字段全量展示**（含未启用项），并叠加运行态（坐标/状态/任务）。
/// 配置字段整包绑定到 <see cref="Config"/>（记录对象），配置变更时替换整个对象即可刷新。
/// </summary>
public partial class CraneConfigRowViewModel : ObservableObject
{
    /// <summary>设备查询委托，用于把任务的源/目标设备号解析成名称。</summary>
    private readonly Func<int, DeviceBase?>? _deviceLookup;

    /// <summary>天车号（主键）。</summary>
    public int Id { get; }

    /// <summary>数据库配置对象（全字段）。</summary>
    [ObservableProperty]
    private CraneConfigDto _config;

    /// <summary>通信状态文本（运行态）。</summary>
    [ObservableProperty]
    private string _connectionText = "未启用";

    /// <summary>X 坐标。</summary>
    [ObservableProperty]
    private double _x;

    /// <summary>Y 坐标。</summary>
    [ObservableProperty]
    private double _y;

    /// <summary>Z 坐标。</summary>
    [ObservableProperty]
    private double _z;

    /// <summary>天车状态文本。</summary>
    [ObservableProperty]
    private string _stateText = "-";

    /// <summary>当前任务简述。</summary>
    [ObservableProperty]
    private string _taskText = "无";

    /// <summary>报警文本。</summary>
    [ObservableProperty]
    private string _alarmText = "-";

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="config">数据库配置</param>
    /// <param name="runtime">运行态天车（未启用/未装载时为 null）</param>
    /// <param name="deviceLookup">设备查询委托</param>
    public CraneConfigRowViewModel(CraneConfigDto config, Crane? runtime, Func<int, DeviceBase?>? deviceLookup = null)
    {
        Id = config.Id;
        _config = config;
        _deviceLookup = deviceLookup;
        ApplyRuntime(runtime);
    }

    /// <summary>更新数据库配置字段。</summary>
    /// <param name="config">新配置</param>
    public void ApplyConfig(CraneConfigDto config) => Config = config;

    /// <summary>更新运行态字段。</summary>
    /// <param name="crane">运行态天车；null 表示未启用/未装载</param>
    public void ApplyRuntime(Crane? crane)
    {
        if (crane is null)
        {
            ConnectionText = "未启用";
            X = Y = Z = 0;
            StateText = "-";
            TaskText = "无";
            AlarmText = "-";
            return;
        }

        var signals = crane.Signals;
        ConnectionText = crane.IsConnected ? "已连接" : "未连接";
        X = signals?.GetDouble("X") ?? 0;
        Y = signals?.GetDouble("Y") ?? 0;
        Z = signals?.GetDouble("Z") ?? 0;
        StateText = DisplayText.CraneStateText(crane.Runtime.State);
        AlarmText = signals?.GetBool("Alarm") == true ? "报警" : "正常";

        var task = crane.Runtime.CurrentTask;
        TaskText = task is null
            ? "无"
            : $"{_deviceLookup?.Invoke(task.SourceDeviceId)?.Name ?? $"设备{task.SourceDeviceId}"}→" +
              $"{_deviceLookup?.Invoke(task.TargetDeviceId)?.Name ?? $"设备{task.TargetDeviceId}"}";
    }
}
