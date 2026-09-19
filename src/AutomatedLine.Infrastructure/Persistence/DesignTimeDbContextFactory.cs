using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AutomatedLine.Infrastructure.Persistence;

/// <summary>
/// 设计时（dotnet ef 命令）用的 DbContext 工厂。
/// 仅在"生成迁移/更新数据库"时被 EF 工具调用，程序运行时不会用到它。
/// 这里刻意使用固定版本的 ServerVersion 而不是 AutoDetect，
/// 因为生成迁移（migrations add）不需要连库，AutoDetect 会强制连库导致离线失败。
/// 真正的连接串在 Wpf 的 appsettings.json 中，运行时由 ConfigService 提供。
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    /// <summary>
    /// 创建用于设计时操作的 DbContext。
    /// </summary>
    /// <param name="args">命令行参数（EF 工具传入，本实现不使用）</param>
    /// <returns>已配置好的 AppDbContext</returns>
    public AppDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();

        // 设计时占位连接串：只用于让 EF 推断 Provider，不会真正连库（除非执行 database update）
        const string designTimeConnection = "server=localhost;port=3306;database=automated_line;user=root;password=;";
        optionsBuilder.UseMySql(designTimeConnection, new MySqlServerVersion(new Version(8, 0, 36)));

        return new AppDbContext(optionsBuilder.Options);
    }
}
