namespace AutomatedLine.Domain.Devices;

/// <summary>
/// 设备类型枚举。
/// 与具体实例类（<c>LatheMachine</c> 等）对应：一种类型可以实例化多台（如斜床 N 台）。
/// 具体哪些类型存在、各有几台，由数据库 DeviceConfig 配置决定，程序不写死（见架构文档 §3.1）。
/// </summary>
public enum DeviceType
{
    /// <summary>未知类型。配置缺失或解析失败时的兜底值，不参与调度。</summary>
    Unknown = 0,

    /// <summary>上料架。仅上料，无加工。</summary>
    LoadRack = 1,

    /// <summary>货叉。是否参与搬运以现场为准。</summary>
    ForkMachine = 2,

    /// <summary>双头镗。</summary>
    BoringMachine = 3,

    /// <summary>斜床（N 台）。</summary>
    LatheMachine = 4,

    /// <summary>研磨机（N 台）。</summary>
    GrinderMachine = 5,

    /// <summary>动平衡机。</summary>
    BalancingMachine = 6,

    /// <summary>打号机。是否上线以现场为准。</summary>
    MarkerMachine = 7,

    /// <summary>验点机。是否上线以现场为准。</summary>
    CheckMachine = 8,

    /// <summary>下料架。仅下料。</summary>
    UnloadRack = 9
}
