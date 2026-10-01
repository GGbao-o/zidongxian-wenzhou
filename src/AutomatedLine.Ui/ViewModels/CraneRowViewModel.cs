using System.Text;
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Domain.Cranes;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Ui.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutomatedLine.Ui.ViewModels;

/// <summary>
/// 天车列表/大屏中的一行。把天车对象与信号快照翻译成界面文本。
/// 大屏悬浮详情（当前任务、工件、source→target）也由此提供。
/// </summary>
public partial class CraneRowViewModel : ObservableObject
{
    /// <summary>设备查询委托，用于把任务的源/目标设备号解析成名称与位置。</summary>
    private readonly Func<int, DeviceBase?>? _deviceLookup;

    /// <summary>天车号。</summary>
    public int Id { get; }

    /// <summary>序号。</summary>
    [ObservableProperty]
    private int _no;

    /// <summary>名称。</summary>
    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>通信状态文本。</summary>
    [ObservableProperty]
    private string _connectionText = "未连接";

    /// <summary>X 坐标。</summary>
    [ObservableProperty]
    private double _x;

    /// <summary>Y 坐标。</summary>
    [ObservableProperty]
    private double _y;

    /// <summary>Z 坐标。</summary>
    [ObservableProperty]
    private double _z;

    /// <summary>状态文本。</summary>
    [ObservableProperty]
    private string _stateText = string.Empty;

    /// <summary>当前任务文本（源→目标）。</summary>
    [ObservableProperty]
    private string _taskText = "无";

    /// <summary>当前工件详情文本（版号/序号/长度/直径）。</summary>
    [ObservableProperty]
    private string _workpieceInfoText = "无工件";

    /// <summary>持有路权区间文本。</summary>
    [ObservableProperty]
    private string _rightOfWayText = "无";

    /// <summary>卡片边框状态（红/黄/绿，优先级见 <see cref="CardStatus"/>）。</summary>
    [ObservableProperty]
    private CardStatus _cardStatus = CardStatus.Normal;

    /// <summary>是否报警（仅展示，不参与边框颜色）。</summary>
    [ObservableProperty]
    private bool _hasAlarm;

    /// <summary>报警文本。</summary>
    [ObservableProperty]
    private string _alarmText = "正常";

    /// <summary>关键信号摘要（供列表页紧凑展示）。</summary>
    [ObservableProperty]
    private string _signalSummary = string.Empty;

    /// <summary>多行信号明细（每个点位一行），供手动操作页与详情展示。</summary>
    [ObservableProperty]
    private string _signalText = string.Empty;

    /// <summary>悬浮详情文本。</summary>
    [ObservableProperty]
    private string _tooltipText = string.Empty;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="crane">天车对象</param>
    /// <param name="deviceLookup">设备查询委托，可为空（列表页可不需要解析设备名）</param>
    /// <param name="status">按需读到的天车状态（null=未读到/未连接）</param>
    public CraneRowViewModel(Crane crane, Func<int, DeviceBase?>? deviceLookup = null, CraneStatusDto? status = null)
    {
        Id = crane.Id;
        _deviceLookup = deviceLookup;
        Refresh(crane, status);
    }

    /// <summary>
    /// 依据天车最新状态（按需读取的结果）刷新展示字段。
    /// </summary>
    /// <param name="crane">天车对象</param>
    /// <param name="status">按需读到的天车状态（null=未读到/未连接）</param>
    public void Refresh(Crane crane, CraneStatusDto? status)
    {
        No = crane.No;
        Name = crane.Name;
        StateText = DisplayText.CraneStateText(crane.Runtime.State);

        ConnectionText = status is not null ? "已连接" : "未连接";
        X = status?.XDisplay ?? 0;
        Y = status?.YDisplay ?? 0;
        Z = status?.ZDisplay ?? 0;
        HasAlarm = status?.HasAlarm == true;
        AlarmText = HasAlarm ? "报警" : "正常";

        // 天车边框颜色优先级：读失败/报警(红) > 有任务(黄) > 空闲正常(绿)
        CardStatus = status is null || status.HasAlarm
            ? CardStatus.Faulted
            : crane.Runtime.CurrentTask is not null
                ? CardStatus.Reserved
                : CardStatus.Normal;

        var info = crane.CurrentWorkpiece?.Info;
        WorkpieceInfoText = info is null
            ? "无工件"
            : $"版号：{info.PlateNo}  序号：{info.SerialNo}\n长度：{info.Length:0.###}  直径：{info.Diameter:0.###}";

        RightOfWayText = crane.Runtime.LockedInterval?.ToString() ?? "无";

        SignalSummary = status is null
            ? "无数据"
            : $"有板={Lvl(status.HasPlate)} 充磁={Lvl(status.MagnetizeFeedback)} 退磁={Lvl(status.DemagnetizeFeedback)} 下压={Lvl(status.PressureLimit)}";

        SignalText = BuildSignalText(status);

        TaskText = BuildTaskShort(crane);
        TooltipText = BuildTooltip(crane);
    }

    /// <summary>
    /// 组装多行信号明细（来自按需读到的状态）。
    /// </summary>
    /// <param name="status">天车状态</param>
    /// <returns>多行文本</returns>
    private static string BuildSignalText(CraneStatusDto? status)
    {
        if (status is null)
        {
            return "无数据";
        }

        var lines = new List<string>
        {
            $"X显示={status.XDisplay}  Y显示={status.YDisplay}  Z显示={status.ZDisplay}",
            $"X编码={status.XEncoder}  Y编码={status.YEncoder}  Z编码={status.ZEncoder}",
            $"回原点 X={Bool(status.XHomeCompleted)} Y={Bool(status.YHomeCompleted)} Z={Bool(status.ZHomeCompleted)}",
            $"伺服报警={status.ServoAlarm}  PLC伺服={status.PlcServoAlarm}  PLC报警={status.PlcAlarm}",
            $"有板={Lvl(status.HasPlate)} 充磁={Lvl(status.MagnetizeFeedback)} 退磁={Lvl(status.DemagnetizeFeedback)} 下压={Lvl(status.PressureLimit)}",
            $"接液盘 开={Lvl(status.DrainOpen)} 关={Lvl(status.DrainClose)}  物理原点 X={Lvl(status.PhysicalHomeX)} Y={Lvl(status.PhysicalHomeY)} Z={Lvl(status.PhysicalHomeZ)}"
        };
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>可空布尔格式化为 PLC 风格 1/0/—（未知）。</summary>
    private static string Lvl(bool? value) => value is null ? "—" : value.Value ? "1" : "0";

    /// <summary>
    /// 列表用任务简述：无任务显示"无"，有任务显示"源→目标设备名"。
    /// </summary>
    /// <param name="crane">天车对象</param>
    /// <returns>任务简述</returns>
    private string BuildTaskShort(Crane crane)
    {
        var task = crane.Runtime.CurrentTask;
        if (task is null)
        {
            return "无";
        }

        var source = ResolveName(task.SourceDeviceId);
        var target = ResolveName(task.TargetDeviceId);
        return $"{source}→{target}";
    }

    /// <summary>
    /// 悬浮详情：任务、工件（板号/序号/直径/长度）、source→target（含设备位置）。
    /// </summary>
    /// <param name="crane">天车对象</param>
    /// <returns>详情文本</returns>
    private string BuildTooltip(Crane crane)
    {
        var builder = new StringBuilder();
        builder.Append(crane.Name).Append("（").Append(DisplayText.CraneStateText(crane.Runtime.State)).Append('）');
        builder.AppendLine();
        builder.Append("连接：").Append(ConnectionText).Append("    路权：").Append(RightOfWayText);
        builder.AppendLine();
        builder.Append("X：").Append(X.ToString("0.###"))
            .Append("  Y：").Append(Y.ToString("0.###"))
            .Append("  Z：").Append(Z.ToString("0.###"));

        var task = crane.Runtime.CurrentTask;
        if (task is null)
        {
            return builder.ToString();
        }

        builder.AppendLine();
        builder.Append("任务：").Append(task.TaskId.ToString("N")[..8]);
        builder.AppendLine();
        builder.Append("源：").Append(DescribeDevice(task.SourceDeviceId, task.SourceX));
        builder.AppendLine();
        builder.Append("目标：").Append(DescribeDevice(task.TargetDeviceId, task.TargetX));

        // 工件信息优先取天车当前持有；任务刚派发尚未取料时可能为空
        var info = crane.CurrentWorkpiece?.Info;
        if (info is not null)
        {
            builder.AppendLine();
            builder.Append("板号：").Append(info.PlateNo)
                .Append("  序号：").Append(info.SerialNo);
            builder.AppendLine();
            builder.Append("直径：").Append(info.Diameter.ToString("0.###"))
                .Append("  长度：").Append(info.Length.ToString("0.###"));
        }

        return builder.ToString();
    }

    /// <summary>解析设备名，找不到时返回"设备#id"。</summary>
    private string ResolveName(int deviceId)
        => _deviceLookup?.Invoke(deviceId)?.Name ?? $"设备{deviceId}";

    /// <summary>描述设备：名称 + X 位置。</summary>
    private string DescribeDevice(int deviceId, double x)
    {
        var device = _deviceLookup?.Invoke(deviceId);
        return device is null
            ? $"设备{deviceId}（X={x:0.###}）"
            : $"{device.Name}（X={device.PositionX:0.###}）";
    }

    /// <summary>布尔点位格式化为 PLC 风格 1/0。</summary>
    private static string Bool(bool value) => value ? "1" : "0";
}
