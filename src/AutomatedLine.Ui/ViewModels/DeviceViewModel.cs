using System.Collections.ObjectModel;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Ui.Helpers;
using AutomatedLine.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutomatedLine.Ui.ViewModels;

/// <summary>
/// 设备管理页面 ViewModel。
/// 展示所有设备的类型、位置、连接、状态、当前工件与信号摘要；由节拍器定期刷新。
/// 配置编辑与详情功能后续补充。
/// </summary>
public partial class DeviceViewModel : ObservableObject
{
    /// <summary>设备注册表。</summary>
    private readonly DeviceRegistry _deviceRegistry;

    /// <summary>设备行集合（界面绑定源）。</summary>
    public ObservableCollection<DeviceRowViewModel> Devices { get; } = new();

    /// <summary>
    /// 构造函数。订阅节拍器定期刷新。
    /// </summary>
    /// <param name="deviceRegistry">设备注册表</param>
    /// <param name="ticker">运行态节拍器</param>
    public DeviceViewModel(DeviceRegistry deviceRegistry, IRuntimeTicker ticker)
    {
        _deviceRegistry = deviceRegistry;
        ticker.Tick += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>
    /// 差量刷新设备行。
    /// </summary>
    private void Refresh()
    {
        var devices = _deviceRegistry.Snapshot().OrderBy(d => d.Id).ToList();
        ObservableCollectionSync.SyncById(
            Devices,
            devices,
            row => row.Id,
            device => device.Id,
            device => new DeviceRowViewModel(device),
            (row, device) => row.Refresh(device));
    }
}
