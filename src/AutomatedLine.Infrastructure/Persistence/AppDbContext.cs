using AutomatedLine.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AutomatedLine.Infrastructure.Persistence;

/// <summary>
/// 应用数据库上下文（EF Core + MySQL）。
/// 只保存配置类数据（天车 / 设备），不保存工件任务与报警运行态（架构文档 §3.4、§14）。
/// 工艺路线顺序固定在代码中（见 Domain 的 ProcessRoute），不进数据库。
/// </summary>
public class AppDbContext : DbContext
{
    /// <summary>
    /// 构造函数。由 DI 注入已配置好的选项。
    /// </summary>
    /// <param name="options">EF Core 选项</param>
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    /// <summary>天车配置表。</summary>
    public DbSet<CraneConfig> CraneConfigs => Set<CraneConfig>();

    /// <summary>设备配置表。</summary>
    public DbSet<DeviceConfig> DeviceConfigs => Set<DeviceConfig>();

    /// <summary>
    /// 模型配置。表名、列长度、枚举存储形式、索引都在这里集中定义，避免散落在实体特性上。
    /// </summary>
    /// <param name="modelBuilder">模型构建器</param>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // 天车配置：名称/协议/IP 限制长度；序号建唯一索引，保证一台车只有一个序号
        modelBuilder.Entity<CraneConfig>(entity =>
        {
            entity.ToTable("CraneConfig");
            entity.HasKey(e => e.Id);
            // 主键由使用方填写（天车号），不由数据库生成；表结构手工维护，兼容自增/非自增两种建表方式
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Name).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Protocol).HasMaxLength(30).IsRequired();
            entity.Property(e => e.IpAddress).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Remark).HasMaxLength(200);
            entity.HasIndex(e => e.No).IsUnique();
        });

        // 设备配置：枚举以字符串存储，便于现场直接读表排错；名称/IP 限制长度
        modelBuilder.Entity<DeviceConfig>(entity =>
        {
            entity.ToTable("DeviceConfig");
            entity.HasKey(e => e.Id);
            // 主键由使用方填写（设备号），不由数据库生成；表结构手工维护，兼容自增/非自增两种建表方式
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Name).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Protocol).HasMaxLength(30).IsRequired();
            entity.Property(e => e.IpAddress).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Remark).HasMaxLength(200);
            entity.Property(e => e.DeviceType).HasConversion<string>().HasMaxLength(30);
        });
    }
}
