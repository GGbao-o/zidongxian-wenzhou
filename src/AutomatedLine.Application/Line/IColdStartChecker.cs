namespace AutomatedLine.Application.Line;

/// <summary>
/// 冷启动核对（架构文档 §10.5）。
/// 启动线路时执行：内存任务为空，但现场某些资源显示"有料"，
/// 说明实物无法与内存任务关联，需产生报警并标记为物理阻塞，避免被误判为空闲。
/// </summary>
public interface IColdStartChecker
{
    /// <summary>
    /// 执行一次冷启动核对。
    /// </summary>
    /// <returns>被标记为物理阻塞的资源描述列表；为空表示无异常</returns>
    IReadOnlyList<string> Check();
}
