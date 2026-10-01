using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Alarms;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Alarms;
using AutomatedLine.Domain.Diagnostics;
using AutomatedLine.Domain.Signals;
using Microsoft.Extensions.Hosting;

namespace AutomatedLine.Application.Collection;

/// <summary>
/// 设备状态采集器实现（架构文档 §8.2、§12.1）。
/// 作为一个后台服务随宿主启动，按配置周期循环采集**设备**；
/// 采集失败按**瞬时故障**处理：写断开快照并记录告警，不丢弃任务、不暂停整线（架构文档 §10.4）。
/// 天车不做后台采集：运动/到位由 CraneService 方法实时读，其余按需调用 ICraneDriver.ReadStatusAsync。
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

    /// <summary>报警管理器。采集失败时产生通信类报警。</summary>
    private readonly AlarmManager _alarmManager;

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
    /// <param name="alarmManager">报警管理器</param>
    public DeviceStateCollector(
        DriverRegistry driverRegistry,
        DeviceRegistry deviceRegistry,
        CraneRegistry craneRegistry,
        IConfigService configService,
        IAppLogger logger,
        AlarmManager alarmManager)
    {
        _driverRegistry = driverRegistry;
        _deviceRegistry = deviceRegistry;
        _craneRegistry = craneRegistry;
        _configService = configService;
        _logger = logger;
        _alarmManager = alarmManager;
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
        var options = _configService.GetAppOptions().Collection;
        var deviceInterval = Math.Max(50, options.DeviceIntervalMs);

        // 只采集设备。天车不再后台轮询：运动/到位由 CraneService 的方法自身实时读取，
        // 调度/预检查/冷启动/界面按需调用 ICraneDriver.ReadStatusAsync（见各消费点）。
        await LoopAsync(cranes: false, deviceInterval, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 单个采集循环：按周期采集设备或天车。
    /// </summary>
    /// <param name="cranes">true 采天车、false 采设备</param>
    /// <param name="intervalMs">周期（毫秒）</param>
    /// <param name="cancellationToken">取消令牌</param>
    private async Task LoopAsync(bool cranes, int intervalMs, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await CollectAsync(cranes, cancellationToken).ConfigureAwait(false);
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
                await Task.Delay(intervalMs, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 执行一轮采集：只遍历指定类别的驱动，读成功后整体替换快照。
    /// </summary>
    /// <param name="cranes">true 采天车、false 采设备</param>
    /// <param name="cancellationToken">取消令牌</param>
    private async Task CollectAsync(bool cranes, CancellationToken cancellationToken)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        var drivers = _driverRegistry.Snapshot()
            .Where(d => cranes ? d is ICraneDriver : d is IDeviceDriver)
            .ToList();
        if (drivers.Count == 0)
        {
            // 该类别暂无驱动，无事可做
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

                // 采集失败属瞬时故障：产生 Warning 报警（合并计数），不丢弃任务、不暂停整线
                var (resourceId, isCrane) = ResolveResource(driver);
                _alarmManager.Raise(
                    AlarmLevel.Warning,
                    Describe(driver),
                    $"采集失败：{ex.Message}",
                    "重连重试",
                    resourceId,
                    isCrane);
            }
        }

        LastCollectedAt = DateTime.Now;

        // 高频循环，成功日志按抽样间隔（默认 20s）落盘，避免刷爆磁盘
        _logger.Sample(LogCategory.Collection, cranes ? "collection-crane" : "collection-device",
            $"采集完成，序号 {sequence}，{(cranes ? "天车" : "设备")} {drivers.Count} 个");
    }

    /// <summary>
    /// 解析驱动对应的资源号与是否天车。
    /// </summary>
    /// <param name="driver">驱动</param>
    /// <returns>资源号与是否天车</returns>
    private static (int? ResourceId, bool IsCrane) ResolveResource(IPlcDriver driver) => driver switch
    {
        IDeviceDriver d => (d.DeviceId, false),
        ICraneDriver c => (c.CraneId, true),
        _ => (null, false)
    };

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
