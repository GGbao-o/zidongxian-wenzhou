using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Configuration;
using Microsoft.Extensions.Configuration;

namespace AutomatedLine.Infrastructure.Config;

/// <summary>
/// 基于 appsettings.json 的配置读取实现（架构文档 §9.3）。
/// 只做"读"，不负责写回；页面参数设置后续在此之上扩展。
/// </summary>
public class ConfigService : IConfigService
{
    /// <summary>配置根对象，由 DI 注入。</summary>
    private readonly IConfiguration _configuration;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="configuration">配置根对象</param>
    public ConfigService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <summary>
    /// 获取 MySQL 连接字符串（读取 ConnectionStrings:MySql）。
    /// </summary>
    /// <returns>连接字符串</returns>
    /// <exception cref="InvalidOperationException">未配置连接字符串时抛出，提示检查配置文件</exception>
    public string GetConnectionString()
    {
        // 连接串必须来自配置文件，禁止硬编码，避免把密码写进代码仓库（架构文档 §9.3）
        var connectionString = _configuration.GetConnectionString("MySql");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "未找到数据库连接字符串 ConnectionStrings:MySql，请检查 appsettings.json。");
        }

        return connectionString;
    }

    /// <summary>
    /// 获取程序参数集合（读取 App 节点）。
    /// </summary>
    /// <returns>参数对象；缺省时返回带默认值的空对象，保证上层无需判空</returns>
    public AppOptions GetAppOptions()
    {
        // Bind 到强类型对象，缺省项使用 AppOptions 的默认值
        var options = _configuration.GetSection("App").Get<AppOptions>();
        return options ?? new AppOptions();
    }
}
