namespace AutomatedLine.Domain.Cranes;

/// <summary>
/// 天车运行状态。
/// 注意：路权在调度器派发时一次性获取（见架构文档 §8.4 / §8.7.2），
/// 因此不存在"等待路权"状态——拿不到路权的任务根本不会被派发。
/// 对应架构文档 §11「CraneState」。
/// </summary>
public enum CraneState
{
    /// <summary>空闲。无任务，且已回到安全位。</summary>
    Idle = 0,

    /// <summary>已分配任务、尚未执行。调度器已派发并占用天车，Worker 即将启动。</summary>
    Reserved = 1,

    /// <summary>执行中。状态机正在推进搬运动作。</summary>
    Running = 2,

    /// <summary>等待设备。已到位但设备侧条件未满足（二次确认未通过等）。</summary>
    WaitingDevice = 3,

    /// <summary>暂停。随线路暂停，可在恢复后继续。</summary>
    Paused = 4,

    /// <summary>故障。状态机终止，任务交由 TaskAbortCoordinator 处理。</summary>
    Faulted = 5,

    /// <summary>不可用。未连接、未启用或被人工置为不可调度。</summary>
    Unavailable = 6
}
