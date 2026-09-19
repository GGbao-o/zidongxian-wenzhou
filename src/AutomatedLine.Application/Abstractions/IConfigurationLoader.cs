namespace AutomatedLine.Application.Abstractions;

/// <summary>
/// 配置装载器：把数据库里的天车/设备配置转换成内存运行时对象与驱动，并注册到各注册表。
/// 这是"数据库配置 → 内存对象"的唯一入口，启动线路前必须先执行一次。
/// </summary>
public interface IConfigurationLoader
{
    /// <summary>
    /// 装载配置。
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>装载结果摘要</returns>
    Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 配置装载结果摘要。
/// </summary>
/// <param name="DeviceCount">装载的设备数</param>
/// <param name="CraneCount">装载的天车数</param>
/// <param name="DriverCount">创建的驱动数</param>
/// <param name="SeededSampleData">是否自动写入了样例数据</param>
public sealed record ConfigurationLoadResult(
    int DeviceCount,
    int CraneCount,
    int DriverCount,
    bool SeededSampleData);
