using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Diagnostics;
using AutomatedLine.Domain.Signals;
using Microsoft.Extensions.Hosting;

namespace AutomatedLine.Application.Collection;

/// <summary>
/// 设备/天车状态采集器实现（架构文档 §8.2、§12.1）。
/// 作为一个后台服务随宿主启动，按配置周期循环采集；
/// 采集失败按**瞬时故障**处理：写断开快照并记录告警，不丢弃任务、不暂停整线（架构文档 §10.4）。
/// </summary>
public sealed class DeviceStateCollector : IDeviceStateCollector, IHostedService
{
    /// <summary>驱动注册表。</summary>
    private readonly DriverRegistry _driverRegistry;

    /// <summary>设备注册表。采集到的快照要回写到对应设备对象。</summary>
    private readonly DeviceRegistry _deviceRegistry;

    /// <summary>天车注册表。采集到的快照要回写到对应天车对象。</summary>
    private readonly CraneRegistry _craneRegistry;

    /// <summary>配置服务，读取采集周期与通信超时。</summary>
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
    /// <param name="craneRegistry">天车注册表</param>
    /// <param name="configService">配置服务</param>
    /// <param name="logger">日志</param>
    public DeviceStateCollector(
        DriverRegistry driverRegistry,
        DeviceRegistry deviceRegistry,
        CraneRegistry craneRegistry,
        IConfigService configService,
        IAppLogger logger)
    {
        _driverRegistry = driverRegistry;
        _deviceRegistry = deviceRegistry;
        _craneRegistry = craneRegistry;
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
        var interval = Math.Max(50, _configService.GetAppOptions().Collection.DeviceIntervalMs);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await CollectOnceAsync(cancellationToken).ConfigureAwait(false);
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
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 执行一轮采集：遍历所有驱动，读成功后整体替换快照。
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    private async Task CollectOnceAsync(CancellationToken cancellationToken)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        var drivers = _driverRegistry.Snapshot();
        if (drivers.Count == 0)
        {
            // 尚未装载配置，无事可做
            return;
        }

        foreach (var driver in drivers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var values = await driver.ReadAllSignalsAsync(cancellationToken).ConfigureAwait(false);
                var snapshot = new SignalSnapshot(
                    DateTime.Now, sequence, ConnectionState.Connected, SignalQuality.Good, values);

                ApplySnapshot(driver, snapshot);
            }
            catch (Exception ex)
            {
                // 瞬时故障：写断开快照，触发后续重连重试，不抛出
                var snapshot = new SignalSnapshot(
                    DateTime.Now, sequence, ConnectionState.Disconnected, SignalQuality.Bad,
                    new Dictionary<string, object?>());

                ApplySnapshot(driver, snapshot);
                _logger.Warn(LogCategory.Communication, $"资源 {Describe(driver)} 采集失败：{ex.Message}");
            }
        }

        LastCollectedAt = DateTime.Now;
    }

    /// <summary>
    /// 把快照整体写到对应的设备或天车对象上（整体替换，不逐字段更新）。
    /// </summary>
    /// <param name="driver">来源驱动</param>
    /// <param name="snapshot">快照</param>
    private void ApplySnapshot(IPlcDriver driver, SignalSnapshot snapshot)
    {
        switch (driver)
        {
            case IDeviceDriver deviceDriver:
                var device = _deviceRegistry.Get(deviceDriver.DeviceId);
                if (device is not null)
                {
                    device.Signals = snapshot;
                }

                break;

            case ICraneDriver craneDriver:
                var crane = _craneRegistry.Get(craneDriver.CraneId);
                if (crane is not null)
                {
                    crane.Signals = snapshot;
                }

                break;
        }
    }

    /// <summary>
    /// 生成用于日志的资源描述。
    /// </summary>
    /// <param name="driver">驱动</param>
    /// <returns>描述文本</returns>
    private static string Describe(IPlcDriver driver) => driver switch
    {
        IDeviceDriver d => $"设备{d.DeviceId}",
        ICraneDriver c => $"天车{c.CraneId}",
        _ => "未知资源"
    };
}
