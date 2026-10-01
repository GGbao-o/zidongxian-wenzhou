using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Diagnostics;
using AutomatedLine.Domain.Signals;
using Microsoft.Extensions.Hosting;

namespace AutomatedLine.Application.Collection;

/// <summary>
/// 设备状态采集器实现（架构文档 §8.2、§12.1）。
/// 每周期**并发**读取所有已装载（数据库启用）的设备，`Task.WhenAll` 全部完成后统一替换缓存。
/// 单台读取失败/超时**只放弃本台本周期**（写断开空快照），不重试、不报警——
/// 报警是异常（天车状态机等）的事，设备采集失败不算报警（架构文档 §10.4）。
/// 天车不做后台采集：运动/到位由 CraneService 方法实时读，其余按需调用 ICraneDriver.ReadStatusAsync。
/// </summary>
public sealed class DeviceStateCollector : IDeviceStateCollector, IHostedService
{
    /// <summary>驱动注册表（只含数据库启用且已装载的设备）。</summary>
    private readonly DriverRegistry _driverRegistry;

    /// <summary>设备注册表。采集到的快照要回写到对应设备对象。</summary>
    private readonly DeviceRegistry _deviceRegistry;

    /// <summary>配置服务，读取采集周期。</summary>
    private readonly IConfigService _configService;

    /// <summary>日志。</summary>
    private readonly IAppLogger _logger;

    /// <summary>取消源。</summary>
    private CancellationTokenSource? _cts;

    /// <summary>采集循环任务。</summary>
    private Task? _loop;

    /// <summary>采集序号。</summary>
    private long _sequence;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="driverRegistry">驱动注册表</param>
    /// <param name="deviceRegistry">设备注册表</param>
    /// <param name="configService">配置服务</param>
    /// <param name="logger">日志</param>
    public DeviceStateCollector(
        DriverRegistry driverRegistry,
        DeviceRegistry deviceRegistry,
        IConfigService configService,
        IAppLogger logger)
    {
        _driverRegistry = driverRegistry;
        _deviceRegistry = deviceRegistry;
        _configService = configService;
        _logger = logger;
    }

    /// <inheritdoc />
    public long Sequence => Interlocked.Read(ref _sequence);

    /// <inheritdoc />
    public DateTime? LastCollectedAt { get; private set; }

    /// <summary>
    /// 启动采集循环。
    /// </summary>
    /// <param name="cancellationToken">宿主取消令牌</param>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        //启动采集
        _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);

        Console.WriteLine("[DeviceStateCollector] 采集循环已启动");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 停止采集循环。
    /// </summary>
    /// <param name="cancellationToken">宿主取消令牌</param>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null)
        {
            return;
        }

        _cts.Cancel();
        if (_loop is not null)
        {
            // 等循环退出，避免宿主停止后仍有后台读取
            await Task.WhenAny(_loop, Task.Delay(Timeout.Infinite, cancellationToken));
        }

        _cts.Dispose();
        _cts = null;
        Console.WriteLine("[DeviceStateCollector] 采集循环已停止");
    }

    /// <summary>
    /// 采集主循环：按周期采集，直到取消。
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var options = _configService.GetAppOptions().Collection;
        var deviceInterval = Math.Max(50, options.DeviceIntervalMs);

        // 只采集设备。天车不再后台轮询：运动/到位由 CraneService 的方法自身实时读取，
        // 调度/预检查/冷启动/界面按需调用 ICraneDriver.ReadStatusAsync（见各消费点）。
        await LoopAsync(deviceInterval, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 单个采集循环：按周期采集设备或天车。
    /// </summary>
    /// <param name="intervalMs">周期（毫秒）</param>
    /// <param name="cancellationToken">取消令牌</param>
    private async Task LoopAsync(int intervalMs, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                //执行一轮采集：**并发**读取所有已装载设备，全部完成后统一替换缓存。
                await CollectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 采集循环本身绝不能因单次异常而退出
                _logger.Error(LogCategory.Communication, "采集循环发生未预期异常", ex);
            }

            try
            {
                //延迟500ms(json配置的采集周期)
                await Task.Delay(intervalMs, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 执行一轮采集：**并发**读取所有已装载设备，全部完成后统一替换缓存。
    /// 单台失败/超时只影响自己（写断开空快照），不重试、不报警。
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    private async Task CollectAsync(CancellationToken cancellationToken)
    {
        var sequence = Interlocked.Increment(ref _sequence);

        // 只含数据库启用且已装载的设备驱动
        var drivers = _driverRegistry.Snapshot().OfType<IDeviceDriver>().ToList();
        if (drivers.Count == 0)
        {
            LastCollectedAt = DateTime.Now;
            return;
        }

        // 每台一个 Task 并发读取（各自 try/catch）；等全部完成（含超时放弃）后再统一替换。
        var tasks = drivers.Select(d => ReadOneAsync(d, sequence, cancellationToken)).ToList();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        foreach (var (driver, snapshot) in results)
        {
            var device = _deviceRegistry.Get(driver.DeviceId);
            if (device is not null)
            {
                device.Signals = snapshot;
            }
        }

        LastCollectedAt = DateTime.Now;

        // 高频循环，成功日志按抽样间隔（默认 20s）落盘，避免刷爆磁盘
        _logger.Sample(LogCategory.Collection, "collection-device",
            $"设备采集完成，序号 {sequence}，共 {drivers.Count} 台");
    }

    /// <summary>
    /// 读取单台设备并生成快照。
    /// 成功→连接/良好快照；失败或超时→断开/空快照（本周期放弃，不重试、不报警）。
    /// </summary>
    /// <param name="driver">设备驱动</param>
    /// <param name="sequence">本周期采集序号</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>驱动与其快照</returns>
    private async Task<(IDeviceDriver Driver, SignalSnapshot Snapshot)> ReadOneAsync(IDeviceDriver driver, long sequence, CancellationToken cancellationToken)
    {
        try
        {
            var values = await driver.ReadAllSignalsAsync(cancellationToken).ConfigureAwait(false);
            return (driver, new SignalSnapshot(DateTime.Now, sequence, ConnectionState.Connected, SignalQuality.Good, values));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn(LogCategory.Communication, $"设备[{driver.DeviceId}] 采集失败（本周期放弃）：{ex.Message}");
            return (driver, new SignalSnapshot(
                DateTime.Now, sequence, ConnectionState.Disconnected, SignalQuality.Bad,
                new Dictionary<string, object?>()));
        }
    }
}
