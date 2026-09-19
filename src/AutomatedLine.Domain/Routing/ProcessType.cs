namespace AutomatedLine.Domain.Routing;

/// <summary>
/// 工序枚举。
/// 工艺顺序以现场确认为准，v1 先按默认固定流程内置（见架构文档 §6.5、§16.1）。
/// 工序描述"加工到哪一步"，与设备类型（<see cref="Devices.DeviceType"/>）是不同维度：
/// 同一道工序可能对应多台同类设备实例。
/// </summary>
public enum ProcessType
{
    /// <summary>未知/未定义工序。兜底值。</summary>
    Unknown = 0,

    /// <summary>上料。工件从上料架进入产线。</summary>
    Loading = 1,

    /// <summary>货叉搬运（如现场货叉参与搬运）。</summary>
    Forking = 2,

    /// <summary>双头镗加工。</summary>
    Boring = 3,

    /// <summary>斜床加工。</summary>
    Lathing = 4,

    /// <summary>研磨加工。</summary>
    Grinding = 5,

    /// <summary>动平衡加工。</summary>
    Balancing = 6,

    /// <summary>打号。</summary>
    Marking = 7,

    /// <summary>验点。</summary>
    Checking = 8,

    /// <summary>下料。工件从下料架下线。</summary>
    Unloading = 9
}
