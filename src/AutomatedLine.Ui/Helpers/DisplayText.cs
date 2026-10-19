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

    /// <summary>设备点位名转中文（用于大屏/详情展示采集到的点位）。未知点名原样返回。</summary>
    /// <param name="name">点位名（驱动点位表的键）</param>
    /// <returns>中文文本</returns>
    public static string SignalNameText(string name) => name switch
    {
        "HasMaterial" => "有料",
        "Alarm" => "报警",
        "RequestData" => "请求数据",
        "RequestLoad" => "请求上料",
        "RequestUnload" => "请求下料",
        "ProcessDone" => "加工完成",
        "Busy" => "忙碌",
        "Running" => "运行中",
        "NewPrintContent" => "有新打印内容",
        "Machining" => "加工中",
        "SafetyDoor" => "安全门",
        "RunState" => "运行状态",
        "AutoMode" => "手自动模式（12063）",
        "LoadLockDone" => "锁紧完成上料退出",
        "UnloadReleaseDone" => "松开完成下料退出",
        "TailstockClamped" => "尾座夹紧到位",
        "TailstockOpened" => "尾座分开到位",
        "DataSentDone" => "数据下发完成",
        "DataTransDone" => "数据传输完成",
        "LoadInPlace" => "上料到达锁紧位置",
        "LoadDone" => "上料完成",
        "UnloadInPlace" => "下料到达松开位置",
        "UnloadDone" => "下料完成",
        "CraneLoadInPlace" => "天车上料到位",
        "CraneLoadDone" => "天车上料完成",
        "CraneUnloadInPlace" => "天车下料到位",
        "CraneUnloadDone" => "天车下料完成",
        _ => name
    };
}
