namespace AutomatedLine.Domain.Line;

/// <summary>
/// 线路（整条产线）运行状态。
/// 由 LineControlService 统一维护，是全线唯一权威状态；天车/设备状态不能替代它。
/// 对应架构文档 §11「LineState」。
/// </summary>
public enum LineState
{
    /// <summary>未启动。程序刚打开或线路被停止，调度与 Worker 均未运行。</summary>
    Stopped = 0,

    /// <summary>启动中。正在做冷启动核对、恢复调度与 Worker，尚未进入自动运行。</summary>
    Starting = 1,

    /// <summary>自动运行。调度器与各天车 Worker 正常推进任务。</summary>
    Running = 2,

    /// <summary>请求暂停。已收到暂停指令，等待当前动作安全收尾后进入 <see cref="Paused"/>。</summary>
    PauseRequested = 3,

    /// <summary>已暂停。人为暂停，可恢复。</summary>
    Paused = 4,

    /// <summary>
    /// 故障暂停。发生硬故障（运动/交互/握手超时、PLC 报警等）后整线暂停，需人工处理后重新启动。
    /// 注意：通信闪断属于瞬时故障，不进入此状态（见架构文档 §10.4）。
    /// </summary>
    PausedByFault = 5
}
