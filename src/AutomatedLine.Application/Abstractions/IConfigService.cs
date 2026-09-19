using AutomatedLine.Application.Configuration;

namespace AutomatedLine.Application.Abstractions;

/// <summary>
/// 程序配置读取抽象。
/// 连接串与运行参数都从这里取，业务层不直接读 JSON 文件（架构文档 §9.3）。
/// </summary>
public interface IConfigService
{
    /// <summary>
    /// 获取 MySQL 连接字符串。
    /// </summary>
    /// <returns>连接字符串；未配置时抛出异常，提示检查 appsettings.json</returns>
    string GetConnectionString();

    /// <summary>
    /// 获取程序参数集合（采集/调度/状态机/日志）。
    /// </summary>
    /// <returns>参数对象，永不返回 null；缺省项使用默认值</returns>
    AppOptions GetAppOptions();
}
