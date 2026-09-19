using System.Collections.Concurrent;
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Configuration;
using AutomatedLine.Domain.Diagnostics;

namespace AutomatedLine.Infrastructure.Logging;

/// <summary>
/// 按天分目录、按类别分文件的文件日志实现（架构文档 §9.4）。
/// 目录结构：Logs/yyyy-MM-dd/{category}.txt。
/// 特性：
/// <list type="bullet">
/// <item>类别细分（采集/生成/调度/状态机/任务/报警/操作等），便于全天运行时检索。</item>
/// <item>单文件超限自动滚动为 {category}_{时间}.txt，避免单文件无限增大。</item>
/// <item>按保留天数自动清理过期目录。</item>
/// <item>支持抽样写入：同一键按间隔节流，满足"采集 500ms、落盘 10~20s"的需求。</item>
/// </list>
/// </summary>
public class FileAppLogger : IAppLogger
{
    /// <summary>写入锁，避免多线程写同一文件造成内容交错。</summary>
    private readonly object _writeLock = new();

    /// <summary>抽样计时锁。</summary>
    private readonly object _sampleLock = new();

    /// <summary>各抽样键上次写入时间。</summary>
    private readonly ConcurrentDictionary<string, DateTime> _lastSampleTimes = new();

    /// <summary>上次执行过期清理的日期，保证每天最多清理一次。</summary>
    private DateTime _lastCleanupDate = DateTime.MinValue;

    /// <summary>日志参数。</summary>
    private readonly LoggingOptions _options;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="configService">配置服务，用于读取日志目录与策略</param>
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

    /// <inheritdoc />
    public void Sample(LogCategory category, string key, string message)
    {
        // 同一键在抽样间隔内只写一次；间隔到点才落盘，降低高频循环的磁盘压力
        var now = DateTime.Now;
        lock (_sampleLock)
        {
            if (_lastSampleTimes.TryGetValue(key, out var last) &&
                (now - last).TotalSeconds < _options.SampleSeconds)
            {
                return;
            }

            _lastSampleTimes[key] = now;
        }

        Write(category, "INFO", message);
    }

    /// <summary>
    /// 实际落盘逻辑：定位目录、按需滚动、清理过期、追加内容。
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

            var today = DateTime.Now.Date;
            var dayDirectory = Path.Combine(rootDirectory, today.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(dayDirectory);

            CleanupExpired(rootDirectory, today);

            var fileName = $"{category.ToString().ToLowerInvariant()}.txt";
            var filePath = Path.Combine(dayDirectory, fileName);

            // 格式：时间 | 级别 | 内容；时间精确到毫秒，便于与采集周期对齐排查
            var line = $"{DateTime.Now:HH:mm:ss.fff} | {level,-5} | {message}{Environment.NewLine}";

            lock (_writeLock)
            {
                // 单文件超限则滚动为带时间戳的历史文件，保证单文件不会无限增大
                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Exists && fileInfo.Length >= _options.MaxFileSizeBytes)
                {
                    var archive = Path.Combine(dayDirectory,
                        $"{category.ToString().ToLowerInvariant()}_{DateTime.Now:HHmmss}.txt");
                    File.Move(filePath, archive, overwrite: true);
                }

                File.AppendAllText(filePath, line);
            }

            // 同时在控制台输出，便于开发期实时观察
            Console.WriteLine($"[{category}] {level}: {message}");
        }
        catch (Exception ex)
        {
            // 日志本身失败绝不能影响业务运行，仅打印到控制台
            Console.WriteLine($"[FileAppLogger] 写入日志失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 清理超过保留天数的日期目录，每天最多执行一次。
    /// </summary>
    /// <param name="rootDirectory">日志根目录</param>
    /// <param name="today">今天</param>
    private void CleanupExpired(string rootDirectory, DateTime today)
    {
        if (_lastCleanupDate == today)
        {
            return;
        }

        _lastCleanupDate = today;
        if (_options.RetentionDays <= 0 || !Directory.Exists(rootDirectory))
        {
            return;
        }

        foreach (var directory in Directory.GetDirectories(rootDirectory))
        {
            var name = Path.GetFileName(directory);
            if (DateTime.TryParse(name, out var date) && (today - date.Date).TotalDays > _options.RetentionDays)
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FileAppLogger] 清理过期日志目录失败 {name}: {ex.Message}");
                }
            }
        }
    }
}
