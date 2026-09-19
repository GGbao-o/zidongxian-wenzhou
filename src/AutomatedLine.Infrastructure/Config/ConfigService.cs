using System.Text.Json;
using System.Text.Json.Nodes;
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Configuration;
using Microsoft.Extensions.Configuration;

namespace AutomatedLine.Infrastructure.Config;

/// <summary>
/// 基于 appsettings.json 的配置读写实现（架构文档 §9.3）。
/// 读取经 IConfiguration；写回只更新 App 节点，保留连接串等其他节点。
/// 宿主配置开启了 reloadOnChange，写盘后 IConfiguration 会自动重载。
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

    /// <summary>
    /// 把程序参数保存回 appsettings.json 的 App 节点。
    /// </summary>
    /// <param name="options">待保存的参数集合</param>
    public void SaveAppOptions(AppOptions options)
    {
        // 写回程序输出目录下的 appsettings.json，与宿主加载的是同一份文件
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var json = File.Exists(path) ? File.ReadAllText(path) : "{}";

        // 用 JsonNode 局部更新，避免覆盖 ConnectionStrings 等不该由界面改动的节点
        var root = JsonNode.Parse(json) as JsonObject ?? new JsonObject();

        var naming = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        root["App"] = JsonSerializer.SerializeToNode(options, naming);

        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("[ConfigService] 已保存程序参数到 appsettings.json");
    }
}
