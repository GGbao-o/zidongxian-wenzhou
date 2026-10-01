using AutomatedLine.Application.Abstractions;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Infrastructure.Communication;
using AutomatedLine.Infrastructure.Persistence.Entities;

namespace AutomatedLine.Infrastructure.Drivers;

/// <summary>
/// 驱动工厂：按设备类型/协议创建真实驱动。
/// 天车：真实汇川 Modbus TCP（<see cref="CraneService"/>，端口/协议写死，IP 来自数据库）。
/// 设备：斜床用 FANUC FOCAS（<see cref="LatheFocasDriver"/>）；双头镗/研磨机/打号机/料架用 Modbus TCP
/// （<see cref="ModbusDeviceDriver"/> + 各设备点位表）。暂未支持的设备类型返回 null（不注册驱动）。
/// </summary>
public static class DriverFactory
{
    /// <summary>
    /// 创建设备驱动。
    /// </summary>
    /// <param name="config">设备配置</param>
    /// <param name="logger">日志</param>
    /// <returns>设备驱动；暂不支持的设备类型返回 null</returns>
    public static IDeviceDriver? CreateDeviceDriver(DeviceConfig config, IAppLogger logger) => config.DeviceType switch
    {
        DeviceType.BoringMachine => new ModbusDeviceDriver(
            config.Id, config.IpAddress, BoringMachineAddress.Port, BoringMachineAddress.UnitId,
            DeviceModbusMaps.Boring(), logger),

        DeviceType.GrinderMachine => new ModbusDeviceDriver(
            config.Id, config.IpAddress, GrinderMachineAddress.Port, GrinderMachineAddress.ResolveUnitId(config.IpAddress),
            DeviceModbusMaps.Grinder(), logger),

        DeviceType.MarkerMachine => new ModbusDeviceDriver(
            config.Id, config.IpAddress, MarkerMachineAddress.Port, MarkerMachineAddress.UnitId,
            DeviceModbusMaps.Marker(), logger),

        DeviceType.LoadRack or DeviceType.UnloadRack => new ModbusDeviceDriver(
            config.Id, config.IpAddress, RackAddress.Port, RackAddress.UnitId,
            DeviceModbusMaps.Rack(config.DeviceType), logger),

        DeviceType.LatheMachine => new LatheFocasDriver(config.Id, config.IpAddress, logger),

        _ => null
    };

    /// <summary>
    /// 创建天车驱动（真实汇川 Modbus TCP）。IP 来自数据库，端口/协议/站号写死；
    /// 运动超时与到位/下压容差来自 appsettings（App:Crane）。
    /// </summary>
    /// <param name="config">天车配置</param>
    /// <param name="logger">日志</param>
    /// <param name="configService">程序配置</param>
    /// <returns>天车驱动</returns>
    public static ICraneDriver CreateCraneDriver(CraneConfig config, IAppLogger logger, IConfigService configService)
    {
        return new CraneService(config.Id, config.Name, config.IpAddress, logger, configService);
    }
}
