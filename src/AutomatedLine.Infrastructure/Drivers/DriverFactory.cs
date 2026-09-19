using AutomatedLine.Application.Abstractions;
using AutomatedLine.Infrastructure.Persistence.Entities;

namespace AutomatedLine.Infrastructure.Drivers;

/// <summary>
/// 驱动工厂：按配置的协议类型创建对应驱动。
/// 目前只实现模拟驱动（Protocol = "Simulated" 或空）；真实协议（Modbus/MC/TCP 等）在 P7 按现场点位补充。
/// 接现场后只需在此追加 case，并删除模拟分支，业务层零改动（架构文档 §9.1）。
/// </summary>
public static class DriverFactory
{
    /// <summary>模拟协议标识。若数据库里没有配协议，也按模拟处理。</summary>
    public const string SimulatedProtocol = "Simulated";

    /// <summary>
    /// 创建设备驱动。
    /// </summary>
    /// <param name="config">设备配置</param>
    /// <returns>设备驱动</returns>
    /// <exception cref="NotSupportedException">协议暂不支持</exception>
    public static IDeviceDriver CreateDeviceDriver(DeviceConfig config)
    {
        if (IsSimulated(config.Protocol))
        {
            return new SimulatedDeviceDriver(config.Id);
        }

        // P7：在此按 config.Protocol 与点位表构造真实设备驱动
        throw new NotSupportedException($"设备 {config.Id} 的协议 [{config.Protocol}] 尚未实现，请先使用 {SimulatedProtocol}。");
    }

    /// <summary>
    /// 创建天车驱动。
    /// </summary>
    /// <param name="config">天车配置</param>
    /// <returns>天车驱动</returns>
    /// <exception cref="NotSupportedException">协议暂不支持</exception>
    public static ICraneDriver CreateCraneDriver(CraneConfig config)
    {
        if (IsSimulated(config.Protocol))
        {
            return new SimulatedCraneDriver(config.Id, config.SafeX);
        }

        // P7：在此按 config.Protocol 与点位表构造真实天车驱动
        throw new NotSupportedException($"天车 {config.Id} 的协议 [{config.Protocol}] 尚未实现，请先使用 {SimulatedProtocol}。");
    }

    /// <summary>
    /// 判断是否按模拟协议处理。
    /// </summary>
    /// <param name="protocol">协议字符串</param>
    /// <returns>是模拟返回 true</returns>
    private static bool IsSimulated(string? protocol)
        => string.IsNullOrWhiteSpace(protocol)
           || string.Equals(protocol, SimulatedProtocol, StringComparison.OrdinalIgnoreCase);
}
