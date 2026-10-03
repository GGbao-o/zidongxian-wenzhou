using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Application.Generation;

/// <summary>
/// 设备握手就绪判定（候选生成用）。
/// 把每类设备的**输入信号**翻译成"能否取料 / 能否放料 / 是否报警"，
/// 供 <see cref="TransportTaskGenerator"/> 判断一条搬运是否当前可执行。
/// 只读 <see cref="DeviceBase.Signals"/>（后台采集快照）；无快照（未采到）一律视为"不就绪"。
/// </summary>
public static class DeviceHandshakeRules
{
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
    /// <item>双头镗/斜床/研磨机：请求下料 `RequestUnload`；</item>
    /// <item>打号机：加工完成 `ProcessDone`。</item>
    /// </list>
    /// </summary>
    /// <param name="device">源设备</param>
    /// <returns>就绪返回 true</returns>
    public static bool CanProvide(DeviceBase device) => device.Type switch
    {
        DeviceType.LoadRack => GetBool(device, "HasMaterial"),
        DeviceType.BoringMachine or DeviceType.LatheMachine or DeviceType.GrinderMachine => GetBool(device, "RequestUnload"),
        DeviceType.MarkerMachine => GetBool(device, "ProcessDone"),
        _ => false
    };

    /// <summary>
    /// 目标设备是否"可放料"（放料就绪）：
    /// <list type="bullet">
    /// <item>双头镗/斜床/研磨机：请求数据 `RequestData`；</item>
    /// <item>打号机：设备空闲（0001 的 **0=空闲**，故判 `Busy == false`）；</item>
    /// <item>下料架：无板（`HasMaterial == false`，空闲可放）。</item>
    /// </list>
    /// </summary>
    /// <param name="device">目标设备</param>
    /// <returns>就绪返回 true</returns>
    public static bool CanReceive(DeviceBase device) => device.Type switch
    {
        DeviceType.BoringMachine or DeviceType.LatheMachine or DeviceType.GrinderMachine => GetBool(device, "RequestData"),
        // 打号机：0001 的 0 才是"空闲"；且必须有采集数据（无快照视为未知，不能当空闲）
        DeviceType.MarkerMachine => device.Signals is not null && !device.Signals.GetBool("Busy"),
        // 下料架：必须有采集数据、且"无板"才算空闲可放（无数据视为未知，不就绪）
        DeviceType.UnloadRack => device.Signals is not null && !device.Signals.GetBool("HasMaterial"),
        _ => false
    };

    /// <summary>读布尔信号；无快照/无点位返回 false。</summary>
    private static bool GetBool(DeviceBase device, string name) => device.Signals?.GetBool(name) == true;
}
