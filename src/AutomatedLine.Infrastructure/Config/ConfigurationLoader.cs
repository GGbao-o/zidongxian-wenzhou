using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Cranes;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Domain.Diagnostics;
using AutomatedLine.Infrastructure.Drivers;
using AutomatedLine.Infrastructure.Persistence;
using AutomatedLine.Infrastructure.Persistence.Entities;
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

        // 联调期：配置表为空时写一套样例数据，保证界面与采集管线立刻可见
        var seeded = false;
        if (options.Simulation.SeedSampleData && !db.CraneConfigs.Any() && !db.DeviceConfigs.Any())
        {
            SeedSampleData(db);
            seeded = true;
            _logger.Info(LogCategory.System, "配置表为空，已写入样例设备/天车数据（供联调）");
        }

        var deviceConfigs = db.DeviceConfigs.AsNoTracking().Where(c => c.IsEnabled).OrderBy(c => c.Id).ToList();
        var craneConfigs = db.CraneConfigs.AsNoTracking().Where(c => c.IsEnabled).OrderBy(c => c.No).ToList();

        var driverCount = 0;

        foreach (var config in deviceConfigs)
        {
            var device = CreateDevice(config);
            _deviceRegistry.Register(device);

            // 目前统一用模拟驱动；接现场后按 config.Protocol 分支构造真实驱动
            var driver = new SimulatedDeviceDriver(config.Id);
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

            var driver = new SimulatedCraneDriver(config.Id, config.SafeX);
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

    /// <summary>
    /// 写入一套样例设备/天车配置（仅供联调，现场接入后请关闭 Simulation.SeedSampleData）。
    /// 坐标参考架构文档 §3.2、§18.1 的示例布局。
    /// </summary>
    /// <param name="db">数据库上下文</param>
    private static void SeedSampleData(AppDbContext db)
    {
        db.DeviceConfigs.AddRange(
            new DeviceConfig { Id = 1, Name = "上料架", DeviceType = DeviceType.LoadRack, Protocol = "Simulated", PositionX = 1000 },
            new DeviceConfig { Id = 2, Name = "双头镗", DeviceType = DeviceType.BoringMachine, Protocol = "Simulated", PositionX = 6000, MinDiameter = 0, MaxDiameter = 1000, MinLength = 0, MaxLength = 5000 },
            new DeviceConfig { Id = 3, Name = "斜床01", DeviceType = DeviceType.LatheMachine, Protocol = "Simulated", PositionX = 12000, MinDiameter = 0, MaxDiameter = 1000, MinLength = 0, MaxLength = 5000 },
            new DeviceConfig { Id = 4, Name = "斜床02", DeviceType = DeviceType.LatheMachine, Protocol = "Simulated", PositionX = 14000, MinDiameter = 0, MaxDiameter = 1000, MinLength = 0, MaxLength = 5000 },
            new DeviceConfig { Id = 5, Name = "研磨机01", DeviceType = DeviceType.GrinderMachine, Protocol = "Simulated", PositionX = 18000, MinDiameter = 0, MaxDiameter = 1000, MinLength = 0, MaxLength = 5000 },
            new DeviceConfig { Id = 6, Name = "动平衡", DeviceType = DeviceType.BalancingMachine, Protocol = "Simulated", PositionX = 22000, MinDiameter = 0, MaxDiameter = 1000, MinLength = 0, MaxLength = 5000 },
            new DeviceConfig { Id = 7, Name = "下料架", DeviceType = DeviceType.UnloadRack, Protocol = "Simulated", PositionX = 26000 });

        db.CraneConfigs.AddRange(
            new CraneConfig { Id = 1, No = 1, Name = "天车1", Protocol = "Simulated", StartX = 0, EndX = 20000, Width = 800, SafeX = 0, SafeZ = 0 },
            new CraneConfig { Id = 2, No = 2, Name = "天车2", Protocol = "Simulated", StartX = 15000, EndX = 30000, Width = 800, SafeX = 30000, SafeZ = 0 });

        db.SaveChanges();
    }
}
