using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace AutomatedLine.Application.Collection;

/// <summary>随宿主运行的天车状态采集服务；每车一个串行循环，跨车独立发布，暂停线路仍持续采集。</summary>
/// 继承 BackgroundService   调用ExecuteAsync()
public sealed class CraneStateCollector : BackgroundService
{
    private readonly CraneRegistry _cranes;
    private readonly DriverRegistry _drivers;
    private readonly CraneStateStore _store;
    private readonly IConfigService _config;
    private readonly IAppLogger _logger;
    private readonly TimeProvider _time;

    /// <summary>创建采集服务；仅调用状态读取，不下发动作、不修改任务或报警处置。</summary>
    /// <param name="cranes">已装载的天车目录。</param>
    /// <param name="drivers">通信驱动目录。</param>
    /// <param name="store">全线唯一快照存储。</param>
    /// <param name="config">采集间隔配置。</param>
    /// <param name="logger">采集运行、失败及恢复日志。</param>
    /// <param name="time">与存储使用同一时钟；未指定时使用系统时钟。</param>
    public CraneStateCollector(CraneRegistry cranes, DriverRegistry drivers, CraneStateStore store,
        IConfigService config, IAppLogger logger, TimeProvider? time = null)
    {
        _cranes = cranes;
        _drivers = drivers;
        _store = store;
        _config = config;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }
/*//学习：对比	StartAsync()	ExecuteAsync()
        所属类型	IHostedService	BackgroundService
        主要作用	启动服务	执行长期后台任务
        谁来调用	宿主框架	BackgroundService 基类
        运行次数	正常每次启动调用一次	正常每次启动调用一次
        能否使用 while 循环	可以，但不宜在其中长期等待	非常适合
        是否必须重写	继承 BackgroundService 时通常不需要	必须实现
        适合业务	初始化、建立必要资源	PLC 采集、调度、监控
        注意：ExecuteAsync() 也只被调用一次，但方法内部可以有一个持续运行的 while 循环。
        而且 StartAsync() 不只存在于后台服务中，你自己定义的普通类也可以有一个同名方法，具体行为取决于代码实现。*/

    /// <summary>维护每车采集循环，观察退出并按当前目录创建循环；单车慢通信不等待其他车。</summary>
    /// <param name="stoppingToken">宿主停止令牌。</param>
    /// <returns>直到宿主停止且所有采集循环结束的任务。</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var collectionToken = lifetime.Token;
        var loops = new Dictionary<int, Task>();
        var intervalMs = Math.Max(50, _config.GetAppOptions().Collection.CraneIntervalMs);
        _logger.Info(LogCategory.Collection, $"天车后台采集启动：每车独立循环，轮间等待 {intervalMs}ms");
        try
        {
            while (!collectionToken.IsCancellationRequested)
            {
                // 观察结束任务后再重建，避免同一车出现两个采集循环；目录新增/移除随下一轮生效。
                foreach (var pair in loops.Where(p => p.Value.IsCompleted).ToList())
                {
                    try { await pair.Value.ConfigureAwait(false); }
                    catch (Exception ex) { _logger.Error(LogCategory.Collection, $"天车[{pair.Key}] 采集循环退出", ex); }
                    loops.Remove(pair.Key);
                }
                foreach (var crane in _cranes.Snapshot().Where(c => c.IsEnabled))
                {
                    if (!loops.ContainsKey(crane.Id))
                        loops[crane.Id] = Task.Run(() => CollectCraneAsync(crane.Id, intervalMs, collectionToken), CancellationToken.None);
                }
                await Task.Delay(intervalMs, collectionToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            lifetime.Cancel();
            // 先结束全部读操作，再让宿主释放驱动；保留循环异常的可见性。
            try { await Task.WhenAll(loops.Values).ConfigureAwait(false); }
            catch (Exception ex) { _logger.Error(LogCategory.Collection, "停止天车采集时循环异常", ex); }
            _logger.Info(LogCategory.Collection, "天车后台采集已停止");
        }
    }

    // 一个循环只负责一台车；失败发布不可用结果，下一轮再尝试，不恢复旧成功状态。
    private async Task CollectCraneAsync(int craneId, int intervalMs, CancellationToken cancellationToken)
    {
        bool? lastSucceeded = null;
        while (!cancellationToken.IsCancellationRequested && _cranes.Get(craneId) is { IsEnabled: true } crane)
        {
            var driver = _drivers.GetCrane(craneId);
            if (driver is not null)
            {
                var startedTimestamp = _time.GetTimestamp();
                var startedAt = _time.GetUtcNow();
                CraneStatusDto? status = null;
                string? failure = null;
                try
                {
                    status = await driver.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (status is null) failure = "驱动未返回有效状态（通信失败或超时）";
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception ex) { failure = ex.Message; }
                //发布本次采集结果
                _store.Publish(driver, status, startedTimestamp, startedAt, failure);
                var maxAgeMs = Math.Max(50, _config.GetAppOptions().Collection.CraneSnapshotMaxAgeMs);
                var succeeded = status is not null && _time.GetElapsedTime(startedTimestamp).TotalMilliseconds <= maxAgeMs;
                if (status is not null && !succeeded) failure = $"本次读取耗时超过快照最大年龄 {maxAgeMs}ms，结果已过期";
                if (lastSucceeded != succeeded)
                {
                    if (succeeded)
                        _logger.Info(LogCategory.Collection, $"天车[{crane.Name}] 状态采集{(lastSucceeded is null ? "首次成功" : "恢复成功")}");
                    else
                        _logger.Warn(LogCategory.Collection, $"天车[{crane.Name}] 状态采集失败，本次缓存不可用：{failure}");
                }
                else
                {
                    _logger.Sample(LogCategory.Collection, $"crane-collect-{craneId}",
                        $"天车[{crane.Name}] 状态采集{(succeeded ? "成功" : "失败")}，耗时 {_time.GetElapsedTime(startedTimestamp).TotalMilliseconds:0}ms" + (failure is null ? "" : $"：{failure}"));
                }
                lastSucceeded = succeeded;
            }
            else if (lastSucceeded != false)
            {
                _logger.Warn(LogCategory.Collection, $"天车[{crane.Name}] 未装载驱动，缓存不可用，等待下一轮检查");
                lastSucceeded = false;
            }
            try { await Task.Delay(intervalMs, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }
}
