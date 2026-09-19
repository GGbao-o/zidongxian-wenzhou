using AutomatedLine.Application.Configuration;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Domain.Signals;
using AutomatedLine.Ui.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutomatedLine.Ui.ViewModels;

/// <summary>
/// 设备管理页的一行：**数据库配置字段全量展示**（含未启用项），并叠加运行态（连接/状态/当前工件/报警）。
/// 配置字段整包绑定到 <see cref="Config"/>。
/// </summary>
public partial class DeviceConfigRowViewModel : ObservableObject
{
    /// <summary>设备号（主键）。</summary>
    public int Id { get; }

    /// <summary>数据库配置对象（全字段）。</summary>
    [ObservableProperty]
    private DeviceConfigDto _config;

    /// <summary>通信状态文本（运行态）。</summary>
    [ObservableProperty]
    private string _connectionText = "未启用";

    /// <summary>资源状态文本。</summary>
    [ObservableProperty]
    private string _stateText = "-";

    /// <summary>当前工件文本。</summary>
    [ObservableProperty]
    private string _workpieceText = "无";

    /// <summary>报警文本。</summary>
    [ObservableProperty]
    private string _alarmText = "-";

    /// <summary>设备类型中文文本（配置字段，便于直接看表）。</summary>
    [ObservableProperty]
    private string _typeText = string.Empty;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="config">数据库配置</param>
    /// <param name="runtime">运行态设备（未启用/未装载时为 null）</param>
    public DeviceConfigRowViewModel(DeviceConfigDto config, DeviceBase? runtime)
    {
        Id = config.Id;
        _config = config;
        TypeText = DisplayText.DeviceTypeText(config.DeviceType);
        ApplyRuntime(runtime);
    }

    /// <summary>更新数据库配置字段。</summary>
    /// <param name="config">新配置</param>
    public void ApplyConfig(DeviceConfigDto config)
    {
        Config = config;
        TypeText = DisplayText.DeviceTypeText(config.DeviceType);
    }

    /// <summary>更新运行态字段。</summary>
    /// <param name="device">运行态设备；null 表示未启用/未装载</param>
    public void ApplyRuntime(DeviceBase? device)
    {
        if (device is null)
        {
            ConnectionText = "未启用";
            StateText = "-";
            WorkpieceText = "无";
            AlarmText = "-";
            return;
        }

        var signals = device.Signals;
        ConnectionText = device.IsConnected ? "已连接" : "未连接";
        StateText = DisplayText.ResourceStateText(device.ResourceState);
        WorkpieceText = device.CurrentWorkpiece?.Info.ToString() ?? "无";
        AlarmText = signals?.GetBool("Alarm") == true ? "报警" : "正常";
    }
}
