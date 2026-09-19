using AutomatedLine.Application.Configuration;

namespace AutomatedLine.Application.Abstractions;

/// <summary>
/// 天车/设备配置的持久化读取与维护（CRUD）。
/// 界面在配置页做增删改后调用本接口写库，再调用 <see cref="IConfigurationLoader"/> 重载运行态。
/// </summary>
public interface IConfigurationStore
{
    /// <summary>读取全部天车配置。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>天车配置列表</returns>
    Task<IReadOnlyList<CraneConfigDto>> GetCraneConfigsAsync(CancellationToken cancellationToken = default);

    /// <summary>新增或更新天车配置（按 Id 判断）。</summary>
    /// <param name="dto">天车配置</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>无</returns>
    Task SaveCraneConfigAsync(CraneConfigDto dto, CancellationToken cancellationToken = default);

    /// <summary>删除天车配置。</summary>
    /// <param name="id">天车号</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>无</returns>
    Task DeleteCraneConfigAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>读取全部设备配置。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>设备配置列表</returns>
    Task<IReadOnlyList<DeviceConfigDto>> GetDeviceConfigsAsync(CancellationToken cancellationToken = default);

    /// <summary>新增或更新设备配置（按 Id 判断）。</summary>
    /// <param name="dto">设备配置</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>无</returns>
    Task SaveDeviceConfigAsync(DeviceConfigDto dto, CancellationToken cancellationToken = default);

    /// <summary>删除设备配置。</summary>
    /// <param name="id">设备号</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>无</returns>
    Task DeleteDeviceConfigAsync(int id, CancellationToken cancellationToken = default);
}
