using AutomatedLine.Domain.Devices;
using AutomatedLine.Domain.Signals;

namespace AutomatedLine.Application.Generation;

/// <summary>
/// 设备握手就绪判定，以及候选生成与派发共用的研磨机自动模式校验。
/// 把每类设备的**输入信号**翻译成"能否取料 / 能否放料 / 是否报警"，
/// 供 <see cref="TransportTaskGenerator"/> 判断一条搬运是否当前可执行。
/// 只读 <see cref="DeviceBase.Signals"/>（后台采集快照）；无快照（未采到）一律视为"不就绪"。
/// </summary>
public static class DeviceHandshakeRules
{
    /// <summary>判断研磨机快照是否明确处于允许自动派发的模式，供业务和页面统一解释原值。</summary>
    /// <param name="signals">同一次设备采集快照。</param>
    /// <returns>连接和质量正常且AutoMode为数值1时返回true；其余返回false。</returns>
    public static bool IsAutomaticMode(SignalSnapshot? signals)
    {
        if (signals is not { IsConnected: true, Quality: SignalQuality.Good }
            || !signals.Values.TryGetValue("AutoMode", out var raw)) return false;
        // 驱动提供原始Word数值；只接受数值1，不能按布尔非零判断，也不能先取整。
        return raw is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal
               && Convert.ToDouble(raw) == 1d;
    }

    /// <summary>查询自动派发的模式阻塞原因；候选生成和入队前共用同一规则，不额外通信。</summary>
    /// <param name="device">待检查的源或目标设备。</param>
    /// <returns>其他设备或研磨机有效模式为1时返回null；否则返回具体阻塞原因。</returns>
    public static string? AutomaticModeBlockReason(DeviceBase device)
    {
        //设备不是研磨机 返回null
        if (device.Type != DeviceType.GrinderMachine) return null;
        var signals = device.Signals;
        if (signals is not { IsConnected: true, Quality: SignalQuality.Good }
            || !signals.Values.TryGetValue("AutoMode", out var raw) || raw is null)
            return "手自动模式未知（12063，要求=1），暂不允许自动派发";

        return IsAutomaticMode(signals) ? null : $"手自动模式不满足（12063={raw}，要求=1），暂不允许自动派发";
    }

    /// <summary>
    /// 设备是否报警（报警的设备不参与生成：源与目标都排除）。
    /// 目前只有研磨机有报警点位（`RunState == 2` 机床报警）。
    /// </summary>
    /// <param name="device">设备</param>
    /// <returns>报警返回 true</returns>
    public static bool IsAlarm(DeviceBase device) => device.Type switch
    {
        DeviceType.GrinderMachine => device.Signals?.GetInt("RunState") == 2,
        _ => false
    };

    /// <summary>
    /// 源设备是否"可取料"（取料就绪）：
    /// <list type="bullet">
    /// <item>上料架：有板 `HasMaterial`；</item>
    /// <item>双头镗/斜床：请求下料 `RequestUnload`；研磨机还须有效AutoMode=1；</item>
    /// <item>打号机：加工完成 `ProcessDone`。</item>
    /// </list>
    /// </summary>
    /// <param name="device">源设备</param>
    /// <returns>就绪返回 true</returns>
    public static bool CanProvide(DeviceBase device) => device.Type switch
    {
        DeviceType.LoadRack => GetBool(device, "HasMaterial"),
        DeviceType.BoringMachine or DeviceType.LatheMachine => GetBool(device, "RequestUnload"),
        DeviceType.GrinderMachine => AutomaticModeBlockReason(device) is null && GetBool(device, "RequestUnload"),
        DeviceType.MarkerMachine => GetBool(device, "ProcessDone"),
        _ => false
    };

    /// <summary>
    /// 目标设备是否"可放料"（放料就绪）：
    /// <list type="bullet">
    /// <item>双头镗/斜床：请求数据 `RequestData`；研磨机还须有效AutoMode=1；</item>
    /// <item>打号机：设备空闲（0001 的 **0=空闲**，故判 `Busy == false`）；</item>
    /// <item>下料架：无板（`HasMaterial == false`，空闲可放）。</item>
    /// </list>
    /// </summary>
    /// <param name="device">目标设备</param>
    /// <returns>就绪返回 true</returns>
    public static bool CanReceive(DeviceBase device) => device.Type switch
    {
        DeviceType.BoringMachine or DeviceType.LatheMachine => GetBool(device, "RequestData"),
        //研磨机条件 
        DeviceType.GrinderMachine => AutomaticModeBlockReason(device) is null && GetBool(device, "RequestData"),
        // 打号机：0001 的 0 才是"空闲"；且必须有采集数据（无快照视为未知，不能当空闲）
        DeviceType.MarkerMachine => device.Signals is not null && !device.Signals.GetBool("Busy"),
        // 下料架：必须有采集数据、且"无板"才算空闲可放（无数据视为未知，不就绪）
        DeviceType.UnloadRack => device.Signals is not null && !device.Signals.GetBool("HasMaterial"),
        _ => false
    };

    /// <summary>读布尔信号；无快照/无点位返回 false。</summary>
    private static bool GetBool(DeviceBase device, string name) => device.Signals?.GetBool(name) == true;
}
