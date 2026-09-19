using AutomatedLine.Application.Abstractions;
using AutomatedLine.Infrastructure.Persistence.Entities;

namespace AutomatedLine.Infrastructure.Drivers;

/// <summary>
/// 驱动工厂。
/// **协议字段（CraneConfig/DeviceConfig.Protocol）只用于数据库展示，不参与驱动选择**；
/// 真正使用哪种驱动在代码里写死：接入现场时直接在这里替换为真实驱动实现（IP/端口从配置读）。
/// 在真实驱动接入前，统一返回模拟驱动，保证未接 PLC 也能跑通流程。
/// </summary>
public static class DriverFactory
{
    /// <summary>
    /// 创建设备驱动。当前统一返回模拟驱动；接入现场时在此改为真实驱动。
    /// </summary>
    /// <param name="config">设备配置</param>
    /// <returns>设备驱动</returns>
    public static IDeviceDriver CreateDeviceDriver(DeviceConfig config)
    {
        return new SimulatedDeviceDriver(config.Id);
    }

    /// <summary>
    /// 创建天车驱动。当前统一返回模拟驱动；接入现场时在此改为真实驱动。
    /// </summary>
    /// <param name="config">天车配置</param>
    /// <returns>天车驱动</returns>
    public static ICraneDriver CreateCraneDriver(CraneConfig config)
    {
        return new SimulatedCraneDriver(config.Id, config.SafeX);
    }
}
