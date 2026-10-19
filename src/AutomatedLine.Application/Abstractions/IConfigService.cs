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

    /// <summary>
    /// 保存程序参数集合到 appsettings.json（仅更新 App 节点，不动连接串等其他节点）。
    /// 参数设置页编辑后调用；宿主配置开启了 reloadOnChange，写盘后会自动重载。
    /// </summary>
    /// <param name="options">待保存的参数集合</param>
    void SaveAppOptions(AppOptions options);

    /// <summary>仅保存工艺模板并同步更新读取结果，保留文件中的其他参数及连接串。</summary>
    /// <param name="options">工艺开关；仅影响后续首次启动的工件。</param>
    void SaveProcessPlanOptions(ProcessPlanOptions options);
}
