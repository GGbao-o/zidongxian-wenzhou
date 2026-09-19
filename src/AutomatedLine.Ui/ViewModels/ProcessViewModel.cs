using System.Collections.ObjectModel;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Routing;
using AutomatedLine.Ui.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutomatedLine.Ui.ViewModels;

/// <summary>
/// 工序展示页面 ViewModel（只读）。
/// 展示内置工艺路线的节点顺序、对应设备类型、是否可选，以及当前该类设备的数量。
/// v1 不允许修改工艺顺序（架构文档 §13.4）。
/// </summary>
public partial class ProcessViewModel : ObservableObject
{
    /// <summary>工艺节点行集合。</summary>
    public ObservableCollection<ProcessNodeRowViewModel> Nodes { get; } = new();

    /// <summary>
    /// 构造函数。依据内置默认工艺路线与已装载设备生成节点行。
    /// </summary>
    /// <param name="deviceRegistry">设备注册表，用于统计各类设备数量</param>
    public ProcessViewModel(DeviceRegistry deviceRegistry)
    {
        var route = ProcessRoute.CreateDefault();
        var devices = deviceRegistry.Snapshot();
        var order = 1;

        foreach (var node in route.Nodes)
        {
            var count = devices.Count(d => d.Type == node.DeviceType);
            Nodes.Add(new ProcessNodeRowViewModel(order++, node, count));
        }
    }
}

/// <summary>
/// 工序节点行。
/// </summary>
public sealed class ProcessNodeRowViewModel
{
    /// <summary>工序顺序（从 1 开始）。</summary>
    public int Order { get; }

    /// <summary>工序名称。</summary>
    public string ProcessText { get; }

    /// <summary>对应设备类型名称。</summary>
    public string DeviceTypeText { get; }

    /// <summary>是否可选。</summary>
    public string OptionalText { get; }

    /// <summary>当前该类设备数量。</summary>
    public int DeviceCount { get; }

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="order">顺序</param>
    /// <param name="node">工艺节点</param>
    /// <param name="deviceCount">设备数量</param>
    public ProcessNodeRowViewModel(int order, ProcessNode node, int deviceCount)
    {
        Order = order;
        ProcessText = DisplayText.ProcessText(node.Process);
        DeviceTypeText = DisplayText.DeviceTypeText(node.DeviceType);
        OptionalText = node.IsOptional ? "可选" : "必需";
        DeviceCount = deviceCount;
    }
}
