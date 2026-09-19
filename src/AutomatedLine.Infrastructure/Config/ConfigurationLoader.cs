using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Cranes;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Domain.Diagnostics;
using AutomatedLine.Infrastructure.Drivers;
using AutomatedLine.Infrastructure.Persistence;
using AutomatedLine.Infrastructure.Persistence.Entities;
using AutomatedLine.Infrastructure.Simulation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AutomatedLine.Infrastructure.Config;

/// <summary>
/// 配置装载器实现：读取数据库配置，构造领域对象与驱动，注册到各注册表。
/// 依赖方向：Infrastructure 引用 Application（注册表接口/实现）与 Domain，符合修正后的引用关系。
/// 通过 <see cref="IServiceScopeFactory"/> 创建作用域获取 scoped 的 AppDbContext，
/// 因此本类本身可注册为单例。
/// </summary>
public class ConfigurationLoader : IConfigurationLoader
{
    /// <summary>作用域工厂，用于获取 AppDbContext。</summary>
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>设备注册表。</summary>
    private readonly DeviceRegistry _deviceRegistry;

    /// <summary>天车注册表。</summary>
    private readonly CraneRegistry _craneRegistry;

    /// <summary>驱动注册表。</summary>
    private readonly DriverRegistry _driverRegistry;

    /// <summary>配置服务。</summary>
    private readonly IConfigService _configService;

    /// <summary>日志。</summary>
    private readonly IAppLogger _logger;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="scopeFactory">作用域工厂</param>
    /// <param name="deviceRegistry">设备注册表</param>
    /// <param name="craneRegistry">天车注册表</param>
    /// <param name="driverRegistry">驱动注册表</param>
    /// <param name="configService">配置服务</param>
    /// <param name="logger">日志</param>
    public ConfigurationLoader(
        IServiceScopeFactory scopeFactory,
        DeviceRegistry deviceRegistry,
        CraneRegistry craneRegistry,
        DriverRegistry driverRegistry,
        IConfigService configService,
        IAppLogger logger)
    {
        _scopeFactory = scopeFactory;
        _deviceRegistry = deviceRegistry;
        _craneRegistry = craneRegistry;
        _driverRegistry = driverRegistry;
        _configService = configService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var options = _configService.GetAppOptions();

        // 联调期样例数据：实现集中在 SimulationDataSeeder，接现场前整体删除该类即可，不影响真实路径
        var seeded = options.Simulation.SeedSampleData && SimulationDataSeeder.SeedIfEmpty(db, _logger);

        var deviceConfigs = db.DeviceConfigs.AsNoTracking().Where(c => c.IsEnabled).OrderBy(c => c.Id).ToList();
        var craneConfigs = db.CraneConfigs.AsNoTracking().Where(c => c.IsEnabled).OrderBy(c => c.No).ToList();

        var driverCount = 0;

        foreach (var config in deviceConfigs)
        {
            var device = CreateDevice(config);
            _deviceRegistry.Register(device);

            // 按协议创建驱动；模拟协议由 DriverFactory 返回模拟实现
            var driver = DriverFactory.CreateDeviceDriver(config);
            await driver.ConnectAsync(cancellationToken).ConfigureAwait(false);
            _driverRegistry.Register(driver);
            driverCount++;
        }

        foreach (var config in craneConfigs)
        {
            var crane = new Crane(config.Id, config.No, config.Name)
            {
                StartX = config.StartX,
                EndX = config.EndX,
                Width = config.Width,
                SafeX = config.SafeX,
                SafeZ = config.SafeZ,
                IsEnabled = config.IsEnabled
            };
            _craneRegistry.Register(crane);

            var driver = DriverFactory.CreateCraneDriver(config);
            await driver.ConnectAsync(cancellationToken).ConfigureAwait(false);
            _driverRegistry.Register(driver);
            driverCount++;
        }

        _logger.Info(LogCategory.System,
            $"配置装载完成：设备 {deviceConfigs.Count} 台、天车 {craneConfigs.Count} 台、驱动 {driverCount} 个、样例数据={seeded}");

        return new ConfigurationLoadResult(deviceConfigs.Count, craneConfigs.Count, driverCount, seeded);
    }

    /// <summary>
    /// 依据设备类型创建对应的领域设备对象，并填充加工范围等参数。
    /// </summary>
    /// <param name="config">设备配置</param>
    /// <returns>设备对象</returns>
    /// <exception cref="NotSupportedException">遇到了未知设备类型</exception>
    private static DeviceBase CreateDevice(DeviceConfig config)
    {
        DeviceBase device = config.DeviceType switch
        {
            DeviceType.LoadRack => new LoadRack(config.Id, config.Name, config.PositionX),
            DeviceType.UnloadRack => new UnloadRack(config.Id, config.Name, config.PositionX),
            DeviceType.ForkMachine => new ForkMachine(config.Id, config.Name, config.PositionX),
            DeviceType.BoringMachine => new BoringMachine(config.Id, config.Name, config.PositionX),
            DeviceType.LatheMachine => new LatheMachine(config.Id, config.Name, config.PositionX),
            DeviceType.GrinderMachine => new GrinderMachine(config.Id, config.Name, config.PositionX),
            DeviceType.BalancingMachine => new BalancingMachine(config.Id, config.Name, config.PositionX),
            DeviceType.MarkerMachine => new MarkerMachine(config.Id, config.Name, config.PositionX),
            DeviceType.CheckMachine => new CheckMachine(config.Id, config.Name, config.PositionX),
            _ => throw new NotSupportedException($"未知设备类型：{config.DeviceType}")
        };

        device.SafePosition = config.SafePosition;
        device.MinDiameter = config.MinDiameter;
        device.MaxDiameter = config.MaxDiameter;
        device.MinLength = config.MinLength;
        device.MaxLength = config.MaxLength;
        device.IsEnabled = config.IsEnabled;
        return device;
    }
}
