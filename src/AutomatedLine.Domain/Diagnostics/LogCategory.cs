namespace AutomatedLine.Domain.Diagnostics;

/// <summary>
/// 日志类别。对应架构文档 §9.4 的日志文件划分：
/// 按天建目录、按类别建文件，便于现场按类型排查问题。
/// </summary>
public enum LogCategory
{
    /// <summary>系统日志：启停、配置加载、冷启动核对等。</summary>
    System = 0,

    /// <summary>通信日志：驱动连接、重连、读写失败等。</summary>
    Communication = 1,

    /// <summary>任务日志：候选生成、派发、状态机推进、异常丢弃等。</summary>
    Task = 2,

    /// <summary>报警日志：与内存报警同步落盘，便于事后追溯。</summary>
    Alarm = 3,

    /// <summary>操作日志：页面上的启停、添加任务、人工清障等操作。</summary>
    Operation = 4
}
