using AutomatedLine.Application.Configuration;
using AutomatedLine.Ui.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutomatedLine.Ui.ViewModels;

/// <summary>
/// 设备管理页的一行：**只展示数据库配置字段**（严格 CRUD，不掺运行态）。
/// </summary>
public partial class DeviceConfigRowViewModel : ObservableObject
{
    /// <summary>设备号（主键）。</summary>
    public int Id { get; }

    /// <summary>数据库配置对象（全字段）。</summary>
    [ObservableProperty]
    private DeviceConfigDto _config;

    /// <summary>设备类型中文文本（由配置派生，便于看表）。</summary>
    [ObservableProperty]
    private string _typeText = string.Empty;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="config">数据库配置</param>
    public DeviceConfigRowViewModel(DeviceConfigDto config)
    {
        Id = config.Id;
        _config = config;
        TypeText = DisplayText.DeviceTypeText(config.DeviceType);
    }

    /// <summary>更新数据库配置字段。</summary>
    /// <param name="config">新配置</param>
    public void ApplyConfig(DeviceConfigDto config)
    {
        Config = config;
        TypeText = DisplayText.DeviceTypeText(config.DeviceType);
    }
}
