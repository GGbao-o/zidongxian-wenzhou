using System.Collections.ObjectModel;
using AutomatedLine.Application.Routing;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Domain.Routing;
using AutomatedLine.Ui.Helpers;
using AutomatedLine.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutomatedLine.Ui.ViewModels;

/// <summary>新工件工艺设置：勾选保存后展示最新路线，只影响之后首次启动的工件。</summary>
public partial class ProcessViewModel : ObservableObject
{
    private readonly DeviceRegistry _devices;
    private readonly IProcessPlanService _plans;
    private readonly IDialogService _dialogs;

    /// <summary>保持原工序先后顺序的设置行。</summary>
    public ObservableCollection<ProcessNodeRowViewModel> Nodes { get; } = new();

    /// <summary>当前新工件工艺的中文预览。</summary>
    [ObservableProperty]
    private string _routeText = string.Empty;

    /// <summary>装配工序开关、模板服务及提示窗口。</summary>
    /// <param name="deviceRegistry">用于显示各类已启用设备数量。</param>
    /// <param name="ticker">界面节拍，仅刷新显示，不触发保存和弹窗。</param>
    /// <param name="plans">最新模板读取、局部保存及首次启动快照服务。</param>
    /// <param name="dialogs">成功路线和保存失败提示。</param>
    public ProcessViewModel(DeviceRegistry deviceRegistry, IRuntimeTicker ticker, IProcessPlanService plans, IDialogService dialogs)
    {
        _devices = deviceRegistry;
        _plans = plans;
        _dialogs = dialogs;
        var order = 1;
        foreach (var node in ProcessRoute.CreateDefault().Nodes) Nodes.Add(new ProcessNodeRowViewModel(order++, node));
        Refresh();
        ticker.Tick += (_, _) => Refresh();
    }

    // 只由用户点击触发；绑定和秒级刷新即使改变IsSelected，也不能反复保存或弹框。
    //核心方法： 工艺管理页面 点击 调用这方法刷新工序
    [RelayCommand]
    private void ToggleProcess(ProcessNodeRowViewModel? row)
    {
        //1. 防御式校验
        if (row is null || !row.CanChange) return;
        // 2. 保存工艺并处理异常
        ProcessPlanSnapshot plan;
        try
        {
            plan = _plans.SetEnabled(row.Process, row.IsSelected);
        }
        catch (Exception ex)
        {
            Refresh();
            _dialogs.ShowMessage($"工艺保存失败，已恢复原勾选：{ex.Message}", "工艺未更新");
            return;
        }
        //刷新
        Refresh();
        _dialogs.ShowMessage($"当前最新工艺路线：\n\n{Format(plan)}\n\n仅对之后首次启动的工件生效；ERP任务在自动入队时生效。已经启动或在设备上的工件继续使用原工艺。", "工艺已更新");
    }

    private void Refresh()
    {
        var devices = _devices.Snapshot();
        var settings = _plans.GetSettings();
        foreach (var row in Nodes)
        {
            row.DeviceCount = devices.Count(device => device.Type == row.DeviceType && device.IsEnabled);
            row.IsSelected = row.IsFixed || (row.DeviceCount > 0 && ProcessPlanService.IsSupported(row.Process)
                && settings.EnabledProcesses.GetValueOrDefault(row.Process, true));
        }
        //更新路线文本
        RouteText = Format(_plans.Preview());
    }

    private static string Format(ProcessPlanSnapshot plan)
        => string.Join(" → ", plan.Nodes.Select(node => DisplayText.ProcessText(node.Process)));
}

/// <summary>工序设置行：加工工序默认选中，上下料固定，无设备或未实现的工序不可启用。</summary>
public partial class ProcessNodeRowViewModel : ObservableObject
{
    /// <summary>现有工序顺序。</summary>
    public int Order { get; }
    /// <summary>工序标识。</summary>
    public ProcessType Process { get; }
    /// <summary>工序名称。</summary>
    public string ProcessText { get; }
    /// <summary>设备类型名称。</summary>
    public string DeviceTypeText { get; }
    /// <summary>对应设备类型。</summary>
    public DeviceType DeviceType { get; }
    /// <summary>上下料是固定起终点，不允许取消。</summary>
    public bool IsFixed => Process is ProcessType.Loading or ProcessType.Unloading;
    /// <summary>加工工序有已实现的启用设备时才允许修改。</summary>
    public bool CanChange => !IsFixed && DeviceCount > 0 && ProcessPlanService.IsSupported(Process);

    /// <summary>新工件是否经过本工序；程序刷新不会触发保存。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private bool _isSelected;

    /// <summary>当前启用设备数量。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChange))]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private int _deviceCount;

    /// <summary>工序参与及设备配置状态，离线不改变勾选。</summary>
    public string StateText => IsFixed ? (DeviceCount > 0 ? "固定工序" : "缺少设备，无法启动")
        : DeviceCount == 0 ? "无设备 · 跳过"
        : !ProcessPlanService.IsSupported(Process) ? "尚未实现 · 不可启用"
        : IsSelected ? "新工件参与" : "新工件跳过";

    /// <summary>创建工序设置行。</summary>
    /// <param name="order">工序顺序。</param>
    /// <param name="node">工序及设备类型。</param>
    public ProcessNodeRowViewModel(int order, ProcessNode node)
    {
        Order = order;
        Process = node.Process;
        DeviceType = node.DeviceType;
        ProcessText = DisplayText.ProcessText(node.Process);
        DeviceTypeText = DisplayText.DeviceTypeText(node.DeviceType);
    }
}
