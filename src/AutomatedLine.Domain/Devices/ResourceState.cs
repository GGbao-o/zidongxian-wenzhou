namespace AutomatedLine.Domain.Devices;

/// <summary>
/// 资源（设备 / 天车）可用性状态。
/// 其中 <see cref="Reserved"/> 是逻辑预约状态，<see cref="BlockedByPhysicalState"/> 来自 PLC 真实物理状态，
/// 二者相互独立、互不替代（见架构文档 §11 注释与 §17 不变式 6）。
/// </summary>
public enum ResourceState
{
    /// <summary>可用。可参与候选任务生成与调度。</summary>
    Available = 0,

    /// <summary>逻辑预约中。已被某个正式任务占用，属于逻辑层状态。</summary>
    Reserved = 1,

    /// <summary>加工/占用中。设备正在加工，或天车正在执行它车任务。</summary>
    Busy = 2,

    /// <summary>报警。PLC 上报了设备/天车报警。</summary>
    Alarm = 3,

    /// <summary>连接断开。通信层不可达；属于瞬时故障，恢复后应能自动消除。</summary>
    Disconnected = 4,

    /// <summary>
    /// 物理状态阻塞。来自 PLC 的真实状态（如仍有未知工件、伴信号异常等）。
    /// 注意：v1.9 起异常处理不再依赖此状态与人工清障——异常时程序自动清空内存并弃板，
    /// 现场残留实物只是"有实物无数据"、不参与排产，无需人工点击恢复。该值保留以备后续扩展。
    /// </summary>
    BlockedByPhysicalState = 5
}
