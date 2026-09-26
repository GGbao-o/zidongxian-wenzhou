using AutomatedLine.Application.Abstractions;
using AutomatedLine.Infrastructure.Communication;
using AutomatedLine.Infrastructure.Persistence.Entities;

namespace AutomatedLine.Infrastructure.Drivers;

/// <summary>
/// 驱动工厂。
/// 天车：使用真实汇川 Modbus TCP 驱动 <see cref="CraneService"/>（端口/协议/站号写死，IP 来自数据库）。
/// 设备：现场协议未定，暂用模拟驱动（后续按现场协议替换）。
/// </summary>
public static class DriverFactory
{
    /// <summary>
    /// 创建设备驱动。设备协议未定，暂返回模拟驱动。
    /// </summary>
    /// <param name="config">设备配置</param>
    /// <returns>设备驱动</returns>
    public static IDeviceDriver CreateDeviceDriver(DeviceConfig config)
    {
        return new SimulatedDeviceDriver(config.Id);
    }

    /// <summary>
    /// 创建天车驱动（真实汇川 Modbus TCP）。IP 来自数据库，端口/协议写死；
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
