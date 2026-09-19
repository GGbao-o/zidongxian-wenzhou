namespace AutomatedLine.Domain.Diagnostics;

/// <summary>
/// 日志类别。对应架构文档 §9.4 的日志文件划分：按天建目录、按类别建文件。
/// 自动线全天运行日志量大，因此类别要细分，便于现场按模块检索与清理。
/// </summary>
public enum LogCategory
{
    /// <summary>系统日志：启停、配置装载、冷启动核对等。</summary>
    System = 0,

    /// <summary>通信日志：驱动连接、重连、读写失败等。</summary>
    Communication = 1,

    /// <summary>采集日志：读取点位/快照（高频，建议用抽样写入）。</summary>
    Collection = 2,

    /// <summary>候选任务生成日志。</summary>
    Generation = 3,

    /// <summary>集中调度与派发日志。</summary>
    Scheduling = 4,

    /// <summary>天车状态机执行日志。</summary>
    StateMachine = 5,

    /// <summary>任务日志：任务生命周期、异常丢弃等。</summary>
    Task = 6,

    /// <summary>报警日志：本质是异常日志，与内存活动报警同步落盘。</summary>
    Alarm = 7,

    /// <summary>操作日志：页面启停、添加任务、手动操作、人工清障等。</summary>
    Operation = 8
}
