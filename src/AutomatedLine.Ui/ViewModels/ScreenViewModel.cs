using System.Collections.ObjectModel;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Ui.Helpers;
using AutomatedLine.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutomatedLine.Ui.ViewModels;

/// <summary>
/// 大屏展示页面 ViewModel（只读，无任何控制按钮）。
/// 展示全部设备与天车的实时信号与内存数据；设备/天车数量可能很多，界面必须支持横竖滚动
/// （架构文档 §13.6）。
/// </summary>
public partial class ScreenViewModel : ObservableObject
{
    /// <summary>设备注册表。</summary>
    private readonly DeviceRegistry _deviceRegistry;

    /// <summary>天车注册表。</summary>
    private readonly CraneRegistry _craneRegistry;

    /// <summary>设备卡片数据。</summary>
    public ObservableCollection<DeviceRowViewModel> Devices { get; } = new();

    /// <summary>天车卡片数据。</summary>
    public ObservableCollection<CraneRowViewModel> Cranes { get; } = new();

    /// <summary>应用服务卡片：候选任务数（TransportTaskGenerator 产物，P3 填充）。</summary>
    [ObservableProperty]
    private int _candidateTaskCount;

    /// <summary>应用服务卡片：已派发正式任务数（集中调度产物，P3 填充）。</summary>
    [ObservableProperty]
    private int _dispatchedTaskCount;

    /// <summary>
    /// 构造函数。订阅节拍器定期刷新。
    /// </summary>
    /// <param name="deviceRegistry">设备注册表</param>
    /// <param name="craneRegistry">天车注册表</param>
    /// <param name="ticker">运行态节拍器</param>
    public ScreenViewModel(DeviceRegistry deviceRegistry, CraneRegistry craneRegistry, IRuntimeTicker ticker)
    {
        _deviceRegistry = deviceRegistry;
        _craneRegistry = craneRegistry;
        ticker.Tick += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>
    /// 差量刷新设备与天车卡片。
    /// </summary>
    private void Refresh()
    {
        ObservableCollectionSync.SyncById(
            Devices,
            _deviceRegistry.Snapshot().OrderBy(d => d.Id).ToList(),
            row => row.Id,
            device => device.Id,
            device => new DeviceRowViewModel(device),
            (row, device) => row.Refresh(device));

        ObservableCollectionSync.SyncById(
            Cranes,
            _craneRegistry.Snapshot().OrderBy(c => c.No).ToList(),
            row => row.Id,
            crane => crane.Id,
            crane => new CraneRowViewModel(crane, _deviceRegistry.Get),
            (row, crane) => row.Refresh(crane));
    }
}
