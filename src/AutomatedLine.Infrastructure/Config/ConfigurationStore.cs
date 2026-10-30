using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Configuration;
using AutomatedLine.Infrastructure.Persistence;
using AutomatedLine.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AutomatedLine.Infrastructure.Config;

/// <summary>
/// 天车/设备配置的数据库 CRUD 实现（EF Core + MySQL）。
/// 经 <see cref="IServiceScopeFactory"/> 创建作用域获取 scoped 的 AppDbContext，本类可注册为单例。
/// 改动后由调用方（界面）触发 <see cref="IConfigurationLoader"/> 重载运行态。
/// </summary>
public class ConfigurationStore : IConfigurationStore
{
    /// <summary>作用域工厂。</summary>
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="scopeFactory">作用域工厂</param>
    public ConfigurationStore(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CraneConfigDto>> GetCraneConfigsAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        // 上下文构造包含同步服务器版本探测；离线或切换连接时也不能阻塞页面首帧。
        var db = await Task.Run(() => scope.ServiceProvider.GetRequiredService<AppDbContext>(), cancellationToken).ConfigureAwait(false);

        var entities = await db.CraneConfigs.AsNoTracking()
            .OrderBy(c => c.No)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return entities.Select(ToDto).ToList();
    }

    /// <inheritdoc />
    public async Task SaveCraneConfigAsync(CraneConfigDto dto, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        // 上下文构造包含同步服务器版本探测；离线或切换连接时也不能阻塞页面首帧。
        var db = await Task.Run(() => scope.ServiceProvider.GetRequiredService<AppDbContext>(), cancellationToken).ConfigureAwait(false);

        var entity = await db.CraneConfigs.FirstOrDefaultAsync(c => c.Id == dto.Id, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            // 新增
            entity = new CraneConfig { Id = dto.Id };
            Apply(dto, entity);
            db.CraneConfigs.Add(entity);
        }
        else
        {
            Apply(dto, entity);
        }

        entity.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteCraneConfigAsync(int id, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        // 上下文构造包含同步服务器版本探测；离线或切换连接时也不能阻塞页面首帧。
        var db = await Task.Run(() => scope.ServiceProvider.GetRequiredService<AppDbContext>(), cancellationToken).ConfigureAwait(false);

        var entity = await db.CraneConfigs.FirstOrDefaultAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false);
        if (entity is not null)
        {
            db.CraneConfigs.Remove(entity);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeviceConfigDto>> GetDeviceConfigsAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        // 上下文构造包含同步服务器版本探测；离线或切换连接时也不能阻塞页面首帧。
        var db = await Task.Run(() => scope.ServiceProvider.GetRequiredService<AppDbContext>(), cancellationToken).ConfigureAwait(false);
        //ToListAsync() 会立即执行 LINQ 查询，把 DeviceConfigs 表的数据真正从数据库拉取到内存里
        var entities = await db.DeviceConfigs.AsNoTracking()
            .OrderBy(d => d.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return entities.Select(ToDto).ToList();
    }

    /// <inheritdoc />
    public async Task SaveDeviceConfigAsync(DeviceConfigDto dto, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        // 上下文构造包含同步服务器版本探测；离线或切换连接时也不能阻塞页面首帧。
        var db = await Task.Run(() => scope.ServiceProvider.GetRequiredService<AppDbContext>(), cancellationToken).ConfigureAwait(false);

        var entity = await db.DeviceConfigs.FirstOrDefaultAsync(d => d.Id == dto.Id, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            entity = new DeviceConfig { Id = dto.Id };
            Apply(dto, entity);
            db.DeviceConfigs.Add(entity);
        }
        else
        {
            Apply(dto, entity);
        }

        entity.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteDeviceConfigAsync(int id, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        // 上下文构造包含同步服务器版本探测；离线或切换连接时也不能阻塞页面首帧。
        var db = await Task.Run(() => scope.ServiceProvider.GetRequiredService<AppDbContext>(), cancellationToken).ConfigureAwait(false);

        var entity = await db.DeviceConfigs.FirstOrDefaultAsync(d => d.Id == id, cancellationToken).ConfigureAwait(false);
        if (entity is not null)
        {
            db.DeviceConfigs.Remove(entity);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>实体 → DTO。</summary>
    private static CraneConfigDto ToDto(CraneConfig e) => new()
    {
        Id = e.Id,
        No = e.No,
        Name = e.Name,
        Protocol = e.Protocol,
        IpAddress = e.IpAddress,
        Port = e.Port,
        StartX = e.StartX,
        EndX = e.EndX,
        Width = e.Width,
        SafeX = e.SafeX,
        SafeZ = e.SafeZ,
        OffsetX = e.OffsetX,
        OffsetY = e.OffsetY,
        OffsetZ = e.OffsetZ,
        SpeedX = e.SpeedX,
        SpeedY = e.SpeedY,
        SpeedZ = e.SpeedZ,
        AccelX = e.AccelX,
        AccelY = e.AccelY,
        AccelZ = e.AccelZ,
        DecelX = e.DecelX,
        DecelY = e.DecelY,
        DecelZ = e.DecelZ,
        IsEnabled = e.IsEnabled
    };

    /// <summary>DTO → 实体（只写字段，不处理主键与时间）。</summary>
    private static void Apply(CraneConfigDto dto, CraneConfig e)
    {
        e.No = dto.No;
        e.Name = dto.Name;
        e.Protocol = dto.Protocol;
        e.IpAddress = dto.IpAddress;
        e.Port = dto.Port;
        e.StartX = dto.StartX;
        e.EndX = dto.EndX;
        e.Width = dto.Width;
        e.SafeX = dto.SafeX;
        e.SafeZ = dto.SafeZ;
        e.OffsetX = dto.OffsetX;
        e.OffsetY = dto.OffsetY;
        e.OffsetZ = dto.OffsetZ;
        e.SpeedX = dto.SpeedX;
        e.SpeedY = dto.SpeedY;
        e.SpeedZ = dto.SpeedZ;
        e.AccelX = dto.AccelX;
        e.AccelY = dto.AccelY;
        e.AccelZ = dto.AccelZ;
        e.DecelX = dto.DecelX;
        e.DecelY = dto.DecelY;
        e.DecelZ = dto.DecelZ;
        e.IsEnabled = dto.IsEnabled;
    }

    /// <summary>实体 → DTO。</summary>
    private static DeviceConfigDto ToDto(DeviceConfig e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        DeviceType = e.DeviceType,
        Protocol = e.Protocol,
        IpAddress = e.IpAddress,
        Port = e.Port,
        PositionX = e.PositionX,
        PositionY = e.PositionY,
        PositionZ = e.PositionZ,
        MinDiameter = e.MinDiameter,
        MaxDiameter = e.MaxDiameter,
        MinLength = e.MinLength,
        MaxLength = e.MaxLength,
        IsEnabled = e.IsEnabled
    };

    /// <summary>DTO → 实体（只写字段，不处理主键与时间）。</summary>
    private static void Apply(DeviceConfigDto dto, DeviceConfig e)
    {
        e.Name = dto.Name;
        e.DeviceType = dto.DeviceType;
        e.Protocol = dto.Protocol;
        e.IpAddress = dto.IpAddress;
        e.Port = dto.Port;
        e.PositionX = dto.PositionX;
        e.PositionY = dto.PositionY;
        e.PositionZ = dto.PositionZ;
        e.MinDiameter = dto.MinDiameter;
        e.MaxDiameter = dto.MaxDiameter;
        e.MinLength = dto.MinLength;
        e.MaxLength = dto.MaxLength;
        e.IsEnabled = dto.IsEnabled;
    }
}
