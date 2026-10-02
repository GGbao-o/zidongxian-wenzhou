using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Configuration;

namespace AutomatedLine.Tests.Fakes;

/// <summary>打桩配置存储：只回答设备坐标。</summary>
public sealed class FakeConfigurationStore : IConfigurationStore
{
    /// <summary>设备坐标（设备号 → DTO）。</summary>
    public List<DeviceConfigDto> Devices { get; } = new();

    /// <inheritdoc />
    public Task<IReadOnlyList<DeviceConfigDto>> GetDeviceConfigsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DeviceConfigDto>>(Devices);

    /// <inheritdoc />
    public Task<IReadOnlyList<CraneConfigDto>> GetCraneConfigsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CraneConfigDto>>(Array.Empty<CraneConfigDto>());

    /// <inheritdoc />
    public Task SaveCraneConfigAsync(CraneConfigDto dto, CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task DeleteCraneConfigAsync(int id, CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task SaveDeviceConfigAsync(DeviceConfigDto dto, CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task DeleteDeviceConfigAsync(int id, CancellationToken ct = default) => Task.CompletedTask;
}
