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

    /// <summary>日志。</summary>
    private readonly IAppLogger _logger;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="scopeFactory">作用域工厂</param>
    /// <param name="deviceRegistry">设备注册表</param>
    /// <param name="craneRegistry">天车注册表</param>
    /// <param name="driverRegistry">驱动注册表</param>
    /// <param name="logger">日志</param>
    public ConfigurationLoader(
        IServiceScopeFactory scopeFactory,
        DeviceRegistry deviceRegistry,
        CraneRegistry craneRegistry,
        DriverRegistry driverRegistry,
        IAppLogger logger)
    {
        _scopeFactory = scopeFactory;
        _deviceRegistry = deviceRegistry;
        _craneRegistry = craneRegistry;
        _driverRegistry = driverRegistry;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        // 重新装载前先清空，保证界面上删除的配置不会残留在内存
        _deviceRegistry.Clear();
        _craneRegistry.Clear();
        _driverRegistry.Clear();

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // 全部配置来自数据库；不再写入任何样例数据（模拟数据已移除）
        var deviceConfigs = db.DeviceConfigs.AsNoTracking().Where(c => c.IsEnabled).OrderBy(c => c.Id).ToList();
        var craneConfigs = db.CraneConfigs.AsNoTracking().Where(c => c.IsEnabled).OrderBy(c => c.No).ToList();

        var driverCount = 0;

        foreach (var config in deviceConfigs)
        {
            var device = CreateDevice(config);
            _deviceRegistry.Register(device);

            // 创建并注册设备驱动（设备号与天车号是两个命名空间，分开注册）。
            // 不在此处主动连接：驱动在首次读/写时惰性连接，避免现场设备离线拖慢甚至阻断配置装载。
            var driver = DriverFactory.CreateDeviceDriver(config);
            _driverRegistry.RegisterDevice(driver);
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
                OffsetX = config.OffsetX,
                OffsetY = config.OffsetY,
                OffsetZ = config.OffsetZ,
                IsEnabled = config.IsEnabled
            };
            _craneRegistry.Register(crane);

            // 同样不主动连接：天车 PLC 离线时不影响配置装载，首次操作时再连。
            var driver = DriverFactory.CreateCraneDriver(config, _logger);
            _driverRegistry.RegisterCrane(driver);
            driverCount++;
        }

        _logger.Info(LogCategory.System,
            $"配置装载完成：设备 {deviceConfigs.Count} 台、天车 {craneConfigs.Count} 台、驱动 {driverCount} 个");

        return Task.FromResult(new ConfigurationLoadResult(deviceConfigs.Count, craneConfigs.Count, driverCount));
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
