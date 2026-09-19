using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Configuration;
using AutomatedLine.Domain.Diagnostics;

namespace AutomatedLine.Infrastructure.Logging;

/// <summary>
/// 按天分目录、按类别分文件的文件日志实现（架构文档 §9.4）。
/// 目录结构：Logs/yyyy-MM-dd/{category}.txt。
/// 采用进程内锁保证并发写入不串行错乱；日志写入失败不得影响主业务（内部吞掉并打印到控制台）。
/// </summary>
public class FileAppLogger : IAppLogger
{
    /// <summary>写入锁，避免多个逻辑循环同时写同一文件造成内容交错。</summary>
    private readonly object _writeLock = new();

    /// <summary>日志参数。</summary>
    private readonly LoggingOptions _options;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="configService">配置服务，用于读取日志目录与保留策略</param>
    public FileAppLogger(IConfigService configService)
    {
        _options = configService.GetAppOptions().Logging;
    }

    /// <summary>写入信息日志。</summary>
    /// <param name="category">日志类别</param>
    /// <param name="message">日志内容</param>
    public void Info(LogCategory category, string message) => Write(category, "INFO", message);

    /// <summary>写入警告日志。</summary>
    /// <param name="category">日志类别</param>
    /// <param name="message">日志内容</param>
    public void Warn(LogCategory category, string message) => Write(category, "WARN", message);

    /// <summary>写入错误日志。</summary>
    /// <param name="category">日志类别</param>
    /// <param name="message">日志内容</param>
    /// <param name="exception">关联异常，可为空</param>
    public void Error(LogCategory category, string message, Exception? exception = null)
    {
        var text = exception is null ? message : $"{message}{Environment.NewLine}{exception}";
        Write(category, "ERROR", text);
    }

    /// <summary>
    /// 实际落盘逻辑。统一在这里拼时间戳、定位目录与文件。
    /// </summary>
    /// <param name="category">日志类别</param>
    /// <param name="level">日志级别文本</param>
    /// <param name="message">日志内容</param>
    private void Write(LogCategory category, string level, string message)
    {
        try
        {
            // 日志根目录以程序运行目录为基准，避免随"从哪个工作目录启动"而漂移
            var rootDirectory = Path.IsPathRooted(_options.Directory)
                ? _options.Directory
                : Path.Combine(AppContext.BaseDirectory, _options.Directory);

            // 以天为目录，便于按日期清理与检索
            var dayDirectory = Path.Combine(rootDirectory, DateTime.Now.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(dayDirectory);

            var fileName = $"{category.ToString().ToLowerInvariant()}.txt";
            var filePath = Path.Combine(dayDirectory, fileName);

            // 格式：时间 | 级别 | 内容；时间精确到毫秒，便于与采集周期对齐排查
            var line = $"{DateTime.Now:HH:mm:ss.fff} | {level,-5} | {message}{Environment.NewLine}";

            lock (_writeLock)
            {
                File.AppendAllText(filePath, line);
            }

            // 同时在控制台输出，便于开发期实时观察（Skill 调试输出规范）
            Console.WriteLine($"[{category}] {level}: {message}");
        }
        catch (Exception ex)
        {
            // 日志本身失败绝不能影响业务运行，仅打印到控制台
            Console.WriteLine($"[FileAppLogger] 写入日志失败: {ex.Message}");
        }
    }
}
