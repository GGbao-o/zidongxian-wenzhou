using System.Diagnostics;
using System.IO;
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Domain.Diagnostics;

namespace AutomatedLine.Ui.Services;

/// <summary>容器建立前也可用的启动日志；固定记录进程号、阶段及耗时，避免初始化失败无日志。</summary>
internal sealed class StartupLog
{
    private readonly object _sync = new();
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private IAppLogger? _logger;

    // 固定备用路径不依赖数据库或appsettings；配置错误时也能定位启动阶段。
    internal void Attach(IAppLogger logger) => _logger = logger;

    internal void Write(string message, Exception? exception = null)
    {
        var detail = $"启动[PID={Environment.ProcessId}，累计{_watch.ElapsedMilliseconds}ms] {message}";
        lock (_sync)
        {
            try
            {
                var directory = Path.Combine(AppContext.BaseDirectory, "Logs", DateTime.Now.ToString("yyyy-MM-dd"));
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "startup.txt"),
                    $"{DateTime.Now:HH:mm:ss.fff} | {(exception is null ? "INFO" : "ERROR")} | {detail}{Environment.NewLine}"
                    + (exception is null ? string.Empty : exception + Environment.NewLine));
            }
            catch (Exception ex) { Trace.WriteLine($"启动日志写入失败：{ex.Message}"); }
            if (exception is null) _logger?.Info(LogCategory.System, detail);
            else _logger?.Error(LogCategory.System, detail, exception);
        }
    }
}
