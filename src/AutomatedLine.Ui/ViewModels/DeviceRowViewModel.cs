using System.Text;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Ui.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutomatedLine.Ui.ViewModels;

/// <summary>
/// 设备列表/大屏中的一行。把设备对象与信号快照翻译成界面文本。
/// 大屏悬浮详情（是否加工、当前工件信息）也由此提供。
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

    /// <summary>加工状态文本（加工中/空闲）。</summary>
    [ObservableProperty]
    private string _machiningText = "空闲";

    /// <summary>当前工件文本（版号/序号）。</summary>
    [ObservableProperty]
    private string _workpieceText = "无";

    /// <summary>当前工件详情文本（版号/序号/长度/直径）。</summary>
    [ObservableProperty]
    private string _workpieceInfoText = "无工件";

    /// <summary>卡片边框状态（红/黄/绿，优先级见 <see cref="CardStatus"/>）。</summary>
    [ObservableProperty]
    private CardStatus _cardStatus = CardStatus.Normal;

    /// <summary>是否报警（仅展示，不参与边框颜色）。</summary>
    [ObservableProperty]
    private bool _hasAlarm;

    /// <summary>报警文本。</summary>
    [ObservableProperty]
    private string _alarmText = "正常";

    /// <summary>一行信号摘要（供列表页紧凑展示）。</summary>
    [ObservableProperty]
    private string _signalSummary = string.Empty;

    /// <summary>多行信号明细（每个点位一行，形如"请求上料=1"），供大屏展示。</summary>
    [ObservableProperty]
    private string _signalText = string.Empty;

    /// <summary>悬浮详情文本。</summary>
    [ObservableProperty]
    private string _tooltipText = string.Empty;

    /// <summary>设备预约查询委托（true=已被任务预约）。</summary>
    private readonly Func<int, bool>? _isReserved;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="device">设备对象</param>
    /// <param name="isReserved">设备预约查询委托，可为空</param>
    /// <param name="displayWorkpiece">外部指定的当前工件（如上料架显示队头），可为空</param>
    public DeviceRowViewModel(DeviceBase device, Func<int, bool>? isReserved = null,
        global::AutomatedLine.Domain.Workpieces.WorkpieceTask? displayWorkpiece = null)
    {
        Id = device.Id;
        _isReserved = isReserved;
        Refresh(device, displayWorkpiece);
    }

    /// <summary>
    /// 依据设备最新状态刷新展示字段。
    /// </summary>
    /// <param name="device">设备对象</param>
    /// <param name="displayWorkpiece">外部指定的当前工件（如上料架显示队头）；为空则用设备内存的当前工件</param>
    public void Refresh(DeviceBase device, global::AutomatedLine.Domain.Workpieces.WorkpieceTask? displayWorkpiece = null)
    {
        Name = device.Name;
        TypeText = DisplayText.DeviceTypeText(device.Type);
        PositionX = device.PositionX;
        StateText = DisplayText.ResourceStateText(device.ResourceState);

        // 上料架的"当前工件"用队头（外部传入）；其余设备用内存的当前工件
        var info = (displayWorkpiece ?? device.CurrentWorkpiece)?.Info;
        WorkpieceText = info?.ToString() ?? "无";
        WorkpieceInfoText = info is null
            ? "无工件"
            : $"版号：{info.PlateNo}  序号：{info.SerialNo}\n长度：{info.Length:0.###}  直径：{info.Diameter:0.###}";

        // 加工状态：当前按"内存是否有工件 + 资源忙"判断；P3 接入调度后由状态机精确维护
        var machining = device.ResourceState == ResourceState.Busy || device.CurrentWorkpiece is not null;
        MachiningText = machining ? "加工中" : "空闲";

        var signals = device.Signals;
        ConnectionText = device.IsConnected ? "已连接" : "未连接";
        HasAlarm = signals?.GetBool("Alarm") == true;
        AlarmText = HasAlarm ? "报警" : "正常";

        // 边框颜色优先级：断开(红) > 已预约(黄) > 正常(绿)
        var reserved = _isReserved?.Invoke(device.Id) ?? false;
        CardStatus = !device.IsConnected
            ? CardStatus.Faulted
            : reserved
                ? CardStatus.Reserved
                : CardStatus.Normal;

        SignalSummary = signals is null
            ? "无数据"
            : $"有料={Bool(signals.GetBool("HasMaterial"))} 请求上料={Bool(signals.GetBool("RequestLoad"))} 请求下料={Bool(signals.GetBool("RequestUnload"))} 完成={Bool(signals.GetBool("ProcessDone"))}";

        SignalText = BuildSignalText(device, signals);
        TooltipText = BuildTooltip(device, machining, signals);
    }

    /// <summary>
    /// 组装多行信号明细：按设备信号点位目录逐点点名，取快照值。
    /// </summary>
    /// <param name="device">设备对象</param>
    /// <param name="signals">信号快照</param>
    /// <returns>多行文本</returns>
    private static string BuildSignalText(DeviceBase device, Domain.Signals.SignalSnapshot? signals)
    {
        if (signals is null)
        {
            return "无数据";
        }

        var builder = new StringBuilder();
        foreach (var point in device.SignalPoints)
        {
            if (!signals.Values.TryGetValue(point.Name, out var value))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(point.Name).Append('=').Append(FormatValue(value));
        }

        return builder.Length == 0 ? "无数据" : builder.ToString();
    }

    /// <summary>
    /// 组装悬浮详情：连接/加工状态 + 当前工件（版号/序号/长度/直径）+ 全部点位信号。
    /// </summary>
    /// <param name="device">设备对象</param>
    /// <param name="machining">是否加工中</param>
    /// <param name="signals">信号快照</param>
    /// <returns>详情文本</returns>
    private static string BuildTooltip(DeviceBase device, bool machining, Domain.Signals.SignalSnapshot? signals)
    {
        var builder = new StringBuilder();
        builder.Append(device.Name).Append("（").Append(DisplayText.DeviceTypeText(device.Type)).Append('）');
        builder.AppendLine();
        builder.Append("连接：").Append(device.IsConnected ? "已连接" : "未连接")
            .Append("    状态：").Append(machining ? "加工中" : "空闲");

        var info = device.CurrentWorkpiece?.Info;
        builder.AppendLine();
        builder.Append("当前工件：");
        if (info is null)
        {
            builder.Append("无");
        }
        else
        {
            builder.AppendLine();
            builder.Append("  版号：").Append(info.PlateNo).Append("  序号：").Append(info.SerialNo);
            builder.AppendLine();
            builder.Append("  长度：").Append(info.Length.ToString("0.###")).Append("  直径：").Append(info.Diameter.ToString("0.###"));
        }

        builder.AppendLine();
        builder.Append("点位信号：");
        if (signals is null)
        {
            builder.Append("无数据");
        }
        else
        {
            foreach (var point in device.SignalPoints)
            {
                if (signals.Values.TryGetValue(point.Name, out var value))
                {
                    builder.AppendLine();
                    builder.Append("  ").Append(point.Name).Append('=').Append(FormatValue(value));
                }
            }
        }

        return builder.ToString();
    }

    /// <summary>布尔点位格式化为 PLC 风格 1/0。</summary>
    private static string Bool(bool value) => value ? "1" : "0";

    /// <summary>信号值格式化。</summary>
    private static string FormatValue(object? value) => value switch
    {
        null => "-",
        bool b => b ? "1" : "0",
        double d => d.ToString("0.###"),
        _ => value.ToString() ?? "-"
    };
}
