using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Configuration;
using AutomatedLine.Domain.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace AutomatedLine.Application.Runtime;

/// <summary>随宿主运行的完成记录清理循环；不依赖页面、线路开关或PLC驱动，仅处理到期Completed工件。</summary>
/// BackgroundService 继承 IHostedService 是一个抽象基类，专门用于实现长时间运行的后台任务。
/// 你需要在派生类中重写 protected override async Task ExecuteAsync(CancellationToken stoppingToken) 方法，把后台服务的全部生命周期逻辑都写在这里面。
public sealed class CompletedTaskCleanupService : BackgroundService
{
    private readonly IRuntimeTaskManager _tasks;
    private readonly DeviceRegistry _devices;
    private readonly CraneRegistry _cranes;
    private readonly IConfigService _config;
    private readonly IAppLogger _logger;
    private readonly TimeProvider _time;

    /// <summary>创建独立清理服务，复用全线任务表及资源目录，不创建任何通信连接。</summary>
    /// <param name="tasks">全线内存任务表。</param>
    /// <param name="devices">设备引用目录。</param>
    /// <param name="cranes">天车引用目录。</param>
    /// <param name="config">每轮读取的清理参数。</param>
    /// <param name="logger">启动、参数变化、批次删除和失败日志。</param>
    /// <param name="time">UTC时钟及等待计时器；省略时使用系统时钟。</param>
    public CompletedTaskCleanupService(IRuntimeTaskManager tasks, DeviceRegistry devices, CraneRegistry cranes,
        IConfigService config, IAppLogger logger, TimeProvider? time = null)
    {
        _tasks = tasks;
        _devices = devices;
        _cranes = cranes;
        _config = config;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>每轮重新读取配置后批量清理；错误仅记录并重试，不暂停线路，不删除其他状态任务。</summary>
    /// 周期性清理已完成的过期任务
    /// <param name="stoppingToken">宿主停止令牌，停止时中断等待。</param>
    /// <returns>宿主停止时结束的后台任务。</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? lastSettings = null;
        var fallbackSeconds = new CompletedTaskCleanupOptions().CheckIntervalSeconds;
        _logger.Info(LogCategory.Operation, "已完成任务自动清理服务启动：独立于页面和线路状态，仅处理有完成时间且到期的Completed任务，不操作PLC");
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var intervalSeconds = fallbackSeconds;
                try
                {
                    var options = _config.GetAppOptions().CompletedTaskCleanup;
                    // 固定本轮参数，避免编辑窗口修改共享对象时一轮内混用新旧值。
                    var enabled = options.Enabled;//默认开启
                    var retention = options.RetentionSeconds;//60s
                    var interval = options.CheckIntervalSeconds;//10s
                    var valid = retention >= 0 && interval > 0 && interval <= int.MaxValue / 1000;
                    var settings = $"启用={enabled}，完成保留={retention}秒，检查间隔={interval}秒";
                    if (settings != lastSettings)
                    {
                        if (valid) _logger.Info(LogCategory.Operation, $"已完成任务自动清理参数：{settings}；0秒保留表示下一轮即可删除，删除后允许同身份再次接收");
                        else _logger.Warn(LogCategory.Operation, $"已完成任务自动清理参数非法：{settings}；保留须非负，检查间隔须为1~{int.MaxValue / 1000}秒，本轮不删除，{fallbackSeconds}秒后重试");
                        lastSettings = settings;
                    }
                    if (valid)
                    {
                        intervalSeconds = interval;
                        if (enabled)
                        {
                            stoppingToken.ThrowIfCancellationRequested();
                            var cutoff = _time.GetUtcNow().AddSeconds(-retention);
                            var removed = _tasks.RemoveCompleted(cutoff, _devices, _cranes);
                            if (removed.Count > 0)
                                _logger.Info(LogCategory.Operation, $"已完成任务自动清理批次完成：删除{removed.Count}件，完成时间≤{cutoff:O}，页面批量通知1次；未保留额外去重记录");
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _logger.Warn(LogCategory.Operation, $"已完成任务自动清理本轮失败：{ex.Message}；保留后续任务，下一轮重试，不改变线路状态");
                }
                // 配置变更在下一轮采用；非法配置采用默认间隔，避免忙循环或结束整个宿主。
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), _time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { _logger.Info(LogCategory.Operation, "已完成任务自动清理服务停止"); }
    }
}
