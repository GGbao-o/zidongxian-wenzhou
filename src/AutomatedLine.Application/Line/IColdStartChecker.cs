namespace AutomatedLine.Application.Line;

/// <summary>
/// 冷启动核对（架构文档 §10.5）。
/// 启动线路时执行：内存任务为空，但现场某些资源显示"有料"，
/// 说明实物无法与内存任务关联；沿用仅报警提示策略，不新增调度阻塞状态。
/// </summary>
public interface IColdStartChecker
{
    /// <summary>
    /// 执行一次冷启动核对；天车并发有界等待有效共享快照，不直接读取 PLC。
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>发现的现场残留资源描述；快照不可用会记录核对未完成日志，空列表不代表全部资源已核对成功。</returns>
    Task<IReadOnlyList<string>> CheckAsync(CancellationToken cancellationToken = default);
}
