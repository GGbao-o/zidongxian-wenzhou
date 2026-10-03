using System.Collections.ObjectModel;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Domain.Routing;
using AutomatedLine.Ui.Helpers;
using AutomatedLine.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutomatedLine.Ui.ViewModels;

/// <summary>
/// 工序展示页面 ViewModel（只读）。
/// 展示内置工艺路线的节点顺序、对应设备类型，以及**该工序当前是否参与工艺**与设备台数。
/// 是否参与的唯一判据是「该设备类型有没有启用设备」（与生成器共用
/// <see cref="ProcessRoute.AvailableDeviceFilter"/>）：没有设备的工序自动跳过，有设备的工序必经。
/// v1 不允许修改工艺顺序（架构文档 §13.4）。
/// </summary>
public partial class ProcessViewModel : ObservableObject
{
    /// <summary>设备注册表。工序是否生效与台数都由它派生。</summary>
    private readonly DeviceRegistry _deviceRegistry;

    /// <summary>内置工艺路线（顺序固定，是否参与现算）。</summary>
    private readonly ProcessRoute _route = ProcessRoute.CreateDefault();

    /// <summary>工艺节点行集合。</summary>
    public ObservableCollection<ProcessNodeRowViewModel> Nodes { get; } = new();

    /// <summary>
    /// 构造函数。依据内置默认工艺路线生成节点行，并按当前设备注册表刷新状态。
    /// </summary>
    /// <param name="deviceRegistry">设备注册表，用于统计各类设备数量</param>
    /// <param name="ticker">界面节拍器（每 1s）：设备增删改后需刷新台数与"是否参与"</param>
    public ProcessViewModel(DeviceRegistry deviceRegistry, IRuntimeTicker ticker)
    {
        _deviceRegistry = deviceRegistry;

        var order = 1;
        foreach (var node in _route.Nodes)
        {
            Nodes.Add(new ProcessNodeRowViewModel(order++, node));
        }

        Refresh();

        // 配置重载会换掉注册表里的设备实例，页面必须跟着刷新；
        // 否则页面显示的台数/是否参与会和实际调度口径不一致（与主页面同一处理方式）
        ticker.Tick += (_, _) => Refresh();
    }

    /// <summary>
    /// 按当前设备注册表刷新每行的"是否参与工艺 + 启用设备台数"。
    /// 就地改属性、不重建集合，避免每秒刷新导致选中行/滚动位置丢失。
    /// </summary>
    private void Refresh()
    {
        var devices = _deviceRegistry.Snapshot();
        var hasDevice = ProcessRoute.AvailableDeviceFilter(devices);

        foreach (var row in Nodes)
        {
            row.IsActive = hasDevice(row.DeviceType);
            row.DeviceCount = row.IsActive
                ? devices.Count(d => d.Type == row.DeviceType && d.IsEnabled)
                : 0;
        }
    }
}

/// <summary>
/// 工序节点行。
/// 设备台数与"是否参与工艺"会随设备配置变化，故做成可观察属性就地更新（不重建集合）。
/// </summary>
public partial class ProcessNodeRowViewModel : ObservableObject
{
    /// <summary>工序顺序（从 1 开始）。</summary>
    public int Order { get; }

    /// <summary>工序名称。</summary>
    public string ProcessText { get; }

    /// <summary>对应设备类型名称。</summary>
    public string DeviceTypeText { get; }

    /// <summary>对应设备类型枚举（供主 ViewModel 统计台数）。</summary>
    public DeviceType DeviceType { get; }

    /// <summary>该工序当前是否参与工艺（该设备类型有启用设备）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private bool _isActive;

    /// <summary>当前该类启用设备数量。</summary>
    [ObservableProperty]
    private int _deviceCount;

    /// <summary>
    /// 工序状态文本：有设备 = 参与工艺；无设备 = 该工序不存在、自动跳过。
    /// </summary>
    public string StateText => IsActive ? "参与工艺" : "无设备 · 跳过";

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="order">顺序</param>
    /// <param name="node">工艺节点</param>
    public ProcessNodeRowViewModel(int order, ProcessNode node)
    {
        Order = order;
        ProcessText = DisplayText.ProcessText(node.Process);
        DeviceTypeText = DisplayText.DeviceTypeText(node.DeviceType);
        DeviceType = node.DeviceType;
    }
}
