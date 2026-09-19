using AutomatedLine.Domain.Diagnostics;

namespace AutomatedLine.Application.Abstractions;

/// <summary>
/// 应用日志抽象。
/// 上层只依赖此接口，具体落盘方式（目录结构、文件滚动）由 Infrastructure 实现，
/// 便于后续替换为第三方日志框架而不影响业务代码（架构文档 §9.4）。
/// </summary>
public interface IAppLogger
{
    /// <summary>
    /// 写入一条普通信息日志。
    /// </summary>
    /// <param name="category">日志类别，决定落到哪个文件</param>
    /// <param name="message">日志内容</param>
    void Info(LogCategory category, string message);

    /// <summary>
    /// 写入一条警告日志。
    /// </summary>
    /// <param name="category">日志类别</param>
    /// <param name="message">日志内容</param>
    void Warn(LogCategory category, string message);

    /// <summary>
    /// 写入一条错误日志，通常伴随异常对象。
    /// </summary>
    /// <param name="category">日志类别</param>
    /// <param name="message">日志内容</param>
    /// <param name="exception">关联异常，可为空</param>
    void Error(LogCategory category, string message, Exception? exception = null);
}
