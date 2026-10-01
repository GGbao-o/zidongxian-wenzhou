using AutomatedLine.Domain.Cranes;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Domain.Routing;
using AutomatedLine.Domain.Workpieces;

namespace AutomatedLine.Ui.Helpers;

/// <summary>
/// 枚举到中文文本的统一映射。
/// 集中在一处，避免各页面 ViewModel 各写一份、后续新增枚举值时漏改。
/// 方法名统一加 Text 后缀，避免与枚举类型同名导致名称解析歧义。
/// </summary>
public static class DisplayText
{
    /// <summary>工序转中文。</summary>
    /// <param name="process">工序</param>
    /// <returns>中文文本</returns>
    public static string ProcessText(ProcessType process) => process switch
    {
        ProcessType.Loading => "上料",
        ProcessType.Forking => "货叉",
        ProcessType.Boring => "双头镗",
        ProcessType.Lathing => "斜床",
        ProcessType.Grinding => "研磨",
        ProcessType.Balancing => "动平衡",
        ProcessType.Marking => "打号",
        ProcessType.Checking => "验点",
        ProcessType.Unloading => "下料",
        _ => "未知"
    };

    /// <summary>设备类型转中文。</summary>
    /// <param name="type">设备类型</param>
    /// <returns>中文文本</returns>
    public static string DeviceTypeText(DeviceType type) => type switch
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

    /// <summary>资源状态转中文。</summary>
    /// <param name="state">资源状态</param>
    /// <returns>中文文本</returns>
    public static string ResourceStateText(ResourceState state) => state switch
    {
        ResourceState.Available => "可用",
        ResourceState.Reserved => "已预约",
        ResourceState.Busy => "加工中",
        ResourceState.Alarm => "报警",
        ResourceState.Disconnected => "断开",
        ResourceState.BlockedByPhysicalState => "物理阻塞",
        _ => "未知"
    };

    /// <summary>天车状态转中文。</summary>
    /// <param name="state">天车状态</param>
    /// <returns>中文文本</returns>
    public static string CraneStateText(CraneState state) => state switch
    {
        CraneState.Idle => "空闲",
        CraneState.Reserved => "已分配",
        CraneState.Running => "执行中",
        CraneState.WaitingDevice => "等待设备",
        CraneState.Paused => "暂停",
        CraneState.Faulted => "故障",
        CraneState.Unavailable => "不可用",
        _ => "未知"
    };

    /// <summary>工件任务状态转中文。</summary>
    /// <param name="state">任务状态</param>
    /// <returns>中文文本</returns>
    public static string WorkpieceStateText(WorkpieceState state) => state switch
    {
        WorkpieceState.Created => "未启动",
        WorkpieceState.Waiting => "排队中",
        WorkpieceState.Paused => "已暂停",
        WorkpieceState.Assigned => "已派发",
        WorkpieceState.Running => "执行中",
        WorkpieceState.Completed => "已完成",
        WorkpieceState.Discarded => "已丢弃",
        _ => "未知"
    };
}
