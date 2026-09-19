using AutomatedLine.Domain.Devices;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutomatedLine.Ui.ViewModels;

/// <summary>
/// 设备列表/大屏中的一行。把设备对象与信号快照翻译成界面文本。
/// </summary>
public partial class DeviceRowViewModel : ObservableObject
{
    /// <summary>设备号。</summary>
    public int Id { get; }

    /// <summary>名称。</summary>
    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>设备类型文本。</summary>
    [ObservableProperty]
    private string _typeText = string.Empty;

    /// <summary>物理 X 位置。</summary>
    [ObservableProperty]
    private double _positionX;

    /// <summary>通信状态文本。</summary>
    [ObservableProperty]
    private string _connectionText = "未连接";

    /// <summary>资源状态文本。</summary>
    [ObservableProperty]
    private string _stateText = string.Empty;

    /// <summary>当前工件文本。</summary>
    [ObservableProperty]
    private string _workpieceText = "无";

    /// <summary>报警文本。</summary>
    [ObservableProperty]
    private string _alarmText = "正常";

    /// <summary>关键信号摘要（供大屏紧凑展示）。</summary>
    [ObservableProperty]
    private string _signalSummary = string.Empty;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="device">设备对象</param>
    public DeviceRowViewModel(DeviceBase device)
    {
        Id = device.Id;
        Refresh(device);
    }

    /// <summary>
    /// 依据设备最新状态刷新展示字段。
    /// </summary>
    /// <param name="device">设备对象</param>
    public void Refresh(DeviceBase device)
    {
        Name = device.Name;
        TypeText = TypeToText(device.Type);
        PositionX = device.PositionX;
        StateText = StateToText(device.ResourceState);
        WorkpieceText = device.CurrentWorkpiece?.Info.ToString() ?? "无";

        var signals = device.Signals;
        ConnectionText = device.IsConnected ? "已连接" : "未连接";
        AlarmText = signals?.GetBool("Alarm") == true ? "报警" : "正常";
        SignalSummary = signals is null
            ? "无数据"
            : $"有料={(signals.GetBool("HasMaterial") ? "是" : "否")} 请求上料={(signals.GetBool("RequestLoad") ? "是" : "否")} 请求下料={(signals.GetBool("RequestUnload") ? "是" : "否")} 完成={(signals.GetBool("ProcessDone") ? "是" : "否")}";
    }

    /// <summary>
    /// 设备类型转中文文本。
    /// </summary>
    /// <param name="type">设备类型</param>
    /// <returns>中文文本</returns>
    private static string TypeToText(DeviceType type) => type switch
    {
        DeviceType.LoadRack => "上料架",
        DeviceType.UnloadRack => "下料架",
        DeviceType.ForkMachine => "货叉",
        DeviceType.BoringMachine => "双头镗",
        DeviceType.LatheMachine => "斜床",
        DeviceType.GrinderMachine => "研磨机",
        DeviceType.BalancingMachine => "动平衡机",
        DeviceType.MarkerMachine => "打号机",
        DeviceType.CheckMachine => "验点机",
        _ => "未知"
    };

    /// <summary>
    /// 资源状态转中文文本。
    /// </summary>
    /// <param name="state">资源状态</param>
    /// <returns>中文文本</returns>
    private static string StateToText(ResourceState state) => state switch
    {
        ResourceState.Available => "可用",
        ResourceState.Reserved => "已预约",
        ResourceState.Busy => "加工中",
        ResourceState.Alarm => "报警",
        ResourceState.Disconnected => "断开",
        ResourceState.BlockedByPhysicalState => "物理阻塞",
        _ => "未知"
    };
}
