using System.Text;
using AutomatedLine.Application.Generation;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Domain.Signals;
using AutomatedLine.Domain.Workpieces;
using AutomatedLine.Ui.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutomatedLine.Ui.ViewModels;

/// <summary>
/// 设备列表/大屏中的一行。把设备对象与信号快照翻译成界面文本。
/// 大屏卡片区分内存工件、PLC加工反馈和预约归属，并提供完整只读信号详情。
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

    /// <summary>PLC加工/忙碌状态，仅有明确反馈点的设备提供，不从内存工件推断。</summary>
    [ObservableProperty]
    private string _machiningText = "未知";

    /// <summary>该设备是否有已接入的PLC加工/忙碌反馈。</summary>
    [ObservableProperty]
    private bool _hasMachiningStatus;

    /// <summary>展示工件存在状态；上料架单独标注队头，避免被误解为设备正在加工。</summary>
    [ObservableProperty]
    private string _workpieceStateText = "无工件";

    /// <summary>卡面工件身份，区分队头与设备内存工件。</summary>
    [ObservableProperty]
    private string _workpieceCaptionText = "内存工件：无";

    /// <summary>卡面单行尺寸。</summary>
    [ObservableProperty]
    private string _dimensionsText = "长度：—  直径：—";

    /// <summary>本轮预约任务对应的天车名称，查不到归属时仅提示已预约。</summary>
    [ObservableProperty]
    private string _reservationText = "未预约";

    /// <summary>根据设备类型筛选的PLC只读信号，不触发通信。</summary>
    [ObservableProperty]
    private IReadOnlyList<CardSignal> _keySignals = Array.Empty<CardSignal>();

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
    /// <param name="reservationOwner">本轮预约归属天车名称，可为空。</param>
    public DeviceRowViewModel(DeviceBase device, Func<int, bool>? isReserved = null,
        global::AutomatedLine.Domain.Workpieces.WorkpieceTask? displayWorkpiece = null, string? reservationOwner = null)
    {
        Id = device.Id;
        _isReserved = isReserved;
        Refresh(device, displayWorkpiece, reservationOwner);
    }

    /// <summary>
    /// 依据设备最新状态刷新展示字段。
    /// </summary>
    /// <param name="device">设备对象</param>
    /// <param name="displayWorkpiece">外部指定的当前工件（如上料架显示队头）；为空则用设备内存的当前工件</param>
    /// <param name="reservationOwner">预约快照关联到的天车名称，不参与预约判断。</param>
    public void Refresh(DeviceBase device, global::AutomatedLine.Domain.Workpieces.WorkpieceTask? displayWorkpiece = null,
        string? reservationOwner = null)
    {
        Name = device.Name;
        TypeText = DisplayText.DeviceTypeText(device.Type);
        PositionX = device.PositionX;

        // 断开或质量不良的快照不应继续把残留值显示为有效PLC信号。
        var signals = device.Signals is { IsConnected: true, Quality: SignalQuality.Good } current ? current : null;
        var alarm = signals?.GetBool("Alarm") == true;
        var reserved = _isReserved?.Invoke(device.Id) == true;

        // 资源状态由事实派生（连接/报警/预约/在制）；DeviceBase.ResourceState 从不写入，已删除
        var state =
            !device.IsConnected ? ResourceState.Disconnected
            : alarm ? ResourceState.Alarm
            : reserved ? ResourceState.Reserved
            : device.CurrentWorkpiece is not null ? ResourceState.Busy
            : ResourceState.Available;
        StateText = DisplayText.ResourceStateText(state);

        // 上料架的"当前工件"用队头（外部传入）；其余设备用内存的当前工件
        var info = (displayWorkpiece ?? device.CurrentWorkpiece)?.Info;
        WorkpieceText = info?.ToString() ?? "无";
        var isQueue = device.Type == DeviceType.LoadRack;
        WorkpieceStateText = info is null ? "无工件" : isQueue ? "队头有工件" : "有工件";
        WorkpieceCaptionText = $"{(isQueue ? "队头工件" : "内存工件")}：{WorkpieceText}";
        DimensionsText = info is null ? "长度：—  直径：—" : $"长度：{info.Length:0.###}  直径：{info.Diameter:0.###}";
        ReservationText = reserved ? reservationOwner ?? "已预约（归属暂不可用）" : "未预约";
        WorkpieceInfoText = info is null
            ? "无工件"
            : $"版号：{info.PlateNo}  序号：{info.SerialNo}\n长度：{info.Length:0.###}  直径：{info.Diameter:0.###}";

        // 双头镗/斜床没有独立加工反馈，不用工件引用或请求信号猜测PLC是否加工。
        HasMachiningStatus = device.Type is DeviceType.GrinderMachine or DeviceType.MarkerMachine;
        MachiningText = device.Type switch
        {
            DeviceType.GrinderMachine => ReadBool(signals, "Machining") switch
                { true => "加工中", false => "加工完成", null => "未知" },
            DeviceType.MarkerMachine => ReadBool(signals, "ProcessDone") == true ? "加工完成"
                : ReadBool(signals, "Busy") switch { true => "忙碌", false => "空闲", null => "未知" },
            _ => "未提供加工信号"
        };
        KeySignals = BuildKeySignals(device.Type, signals);

        ConnectionText = device.IsConnected ? "已连接" : "未连接";
        HasAlarm = alarm;
        AlarmText = HasAlarm ? "报警" : "正常";

        // 边框颜色优先级：断开(红) > 已预约(黄) > 正常(绿)
        CardStatus = !device.IsConnected
            ? CardStatus.Faulted
            : reserved
                ? CardStatus.Reserved
                : CardStatus.Normal;

        SignalSummary = string.Join("  ", KeySignals.Select(signal => signal.Text));

        SignalText = BuildSignalText(device, signals);
        // 列表与悬浮详情使用同一份工件信息，确保上料架显示外部传入的队头。
        TooltipText = BuildTooltip(device, signals, info);
    }

    // 只认实际采集到的布尔值；缺失、null、断开和类型不符都保持未知。
    private static bool? ReadBool(SignalSnapshot? signals, string point)
        => signals is not null && signals.Values.TryGetValue(point, out var value) && value is bool flag ? flag : null;

    // 只展示该机型实际接入的关键点，避免向所有设备硬套同一套信号；完整明细仍留在悬浮框。
    private static IReadOnlyList<CardSignal> BuildKeySignals(DeviceType type, SignalSnapshot? signals)
    {
        CardSignal Point(string label, string point) => new(label, ReadBool(signals, point));
        return type switch
        {
            DeviceType.LoadRack => new[] { Point("允许取料", "HasMaterial") },
            // 下料架采集缓存HasMaterial已反转，放料允许必须再次反转；null仍为null。
            DeviceType.UnloadRack => new[] { new CardSignal("允许放料", !ReadBool(signals, "HasMaterial")) },
            DeviceType.BoringMachine or DeviceType.LatheMachine => new[]
                { Point("请求数据", "RequestData"), Point("请求上料", "RequestLoad"), Point("请求下料", "RequestUnload") },
            DeviceType.GrinderMachine => new[]
                { Point("加工中", "Machining"), Point("请求数据", "RequestData"), Point("请求上料", "RequestLoad"), Point("请求下料", "RequestUnload") },
            DeviceType.MarkerMachine => new[]
                { Point("忙碌", "Busy"), Point("加工完成", "ProcessDone"), Point("运行中", "Running") },
            _ => Array.Empty<CardSignal>()
        };
    }

    /// <summary>
    /// 组装多行信号明细：**直接遍历采集器写入的快照**（`DeviceStateCollector` 后台读到的全部点位）。
    /// </summary>
    /// <param name="device">设备对象</param>
    /// <param name="signals">信号快照</param>
    /// <returns>多行文本</returns>
    private static string BuildSignalText(DeviceBase device, Domain.Signals.SignalSnapshot? signals)
    {
        var modeLine = device.Type == DeviceType.GrinderMachine
            ? $"{DisplayText.SignalNameText("AutoMode")}：{FormatAutomaticMode(signals)}" : null;
        if (signals is null || signals.Values.Count == 0)
        {
            return modeLine is null ? "无数据" : modeLine + Environment.NewLine + "无数据";
        }

        var lines =
            signals.Values.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Where(kv => modeLine is null || kv.Key != "AutoMode")
                .Select(kv => $"{DisplayText.SignalNameText(kv.Key)}={FormatValue(kv.Value)}");
        return string.Join(Environment.NewLine, modeLine is null ? lines : lines.Prepend(modeLine));
    }

    // 与派发规则共用有效快照及严格数值1判定；缺失或失效明确显示未知，不冒充模式0。
    private static string FormatAutomaticMode(SignalSnapshot? signals)
    {
        if (signals is null || !signals.Values.TryGetValue("AutoMode", out var raw) || raw is null) return "未知";
        var automatic = DeviceHandshakeRules.IsAutomaticMode(signals);
        return $"{FormatValue(raw)}（{(automatic ? "允许自动派发" : "不允许自动派发")}）";
    }

    /// <summary>
    /// 组装悬浮详情：连接/工件状态、PLC状态、预约及全部只读信号。
    /// </summary>
    /// <param name="device">设备对象</param>
    /// <param name="signals">信号快照</param>
    /// <param name="info">已选定的展示工件信息；上料架使用队头，其余设备使用内存工件</param>
    /// <returns>详情文本</returns>
    private string BuildTooltip(DeviceBase device, Domain.Signals.SignalSnapshot? signals, WorkpieceInfo? info)
    {
        var builder = new StringBuilder();
        builder.Append(device.Name).Append("（").Append(DisplayText.DeviceTypeText(device.Type)).Append('）');
        builder.AppendLine();
        builder.Append("连接：").Append(device.IsConnected ? "已连接" : "未连接")
            .Append("    工件状态：").Append(WorkpieceStateText);
        if (HasMachiningStatus) builder.Append("    PLC状态：").Append(MachiningText);
        builder.AppendLine().Append("预约：").Append(ReservationText);

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
        builder.AppendLine().Append("  ").Append(BuildSignalText(device, signals)
            .Replace(Environment.NewLine, Environment.NewLine + "  "));

        return builder.ToString();
    }

    /// <summary>信号值格式化。</summary>
    private static string FormatValue(object? value) => value switch
    {
        null => "未知",
        bool b => b ? "1" : "0",
        double d => d.ToString("0.###"),
        _ => value.ToString() ?? "-"
    };
}
