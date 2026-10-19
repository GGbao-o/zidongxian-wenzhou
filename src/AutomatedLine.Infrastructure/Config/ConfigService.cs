using System.Text.Json;
using System.Text.Json.Nodes;
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Configuration;
using Microsoft.Extensions.Configuration;

namespace AutomatedLine.Infrastructure.Config;

/// <summary>
/// 基于 appsettings.json 的配置读写实现（架构文档 §9.3）。
/// 读取经 IConfiguration；普通参数更新 App 节点但保留独立维护的工艺，工艺保存只更新 processPlan。
/// 写盘后同步重载，使保存后紧接着的首次启动能获取最新工艺。
/// </summary>
public class ConfigService : IConfigService
{
    // 参数编辑和工艺勾选共用写锁，防止互相覆盖刚保存的配置。
    private readonly object _saveSync = new();
    private readonly string _settingsPath;
    /// <summary>配置根对象，由 DI 注入。</summary>
    private readonly IConfiguration _configuration;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="configuration">配置根对象</param>
    /// <param name="settingsPath">实际配置文件路径；默认使用程序运行目录，测试可指定独立临时文件。</param>
    public ConfigService(IConfiguration configuration, string? settingsPath = null)
    {
        _configuration = configuration;
        _settingsPath = settingsPath ?? Path.Combine(AppContext.BaseDirectory, "appsettings.json");
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
    /// 它获取的是配置系统内存里的数据，不会在每次调用时直接读取磁盘。
    /// 获取程序参数集合（读取 App 节点）。
    /// </summary>
    /// <returns>参数对象；缺省时返回带默认值的空对象，保证上层无需判空</returns>
    public AppOptions GetAppOptions()
    {
        lock (_saveSync)
        {
            var options = _configuration.GetSection("App").Get<AppOptions>();
            return options ?? new AppOptions();
        }
    }

    /// <summary>
    /// 保存参数并同步重载；工艺节点单独管理，不能被尚未关闭的旧参数编辑窗口覆盖。
    /// </summary>
    /// <param name="options">待保存的参数集合</param>
    public void SaveAppOptions(AppOptions options)
    {
        lock (_saveSync)
        {
            var root = ReadRoot();
            var plan = root["App"]?["processPlan"]?.DeepClone();
            root["App"] = JsonSerializer.SerializeToNode(options, JsonOptions);
            if (plan is not null) root["App"]!["processPlan"] = plan;
            WriteRoot(root);
        }
    }

    /// <inheritdoc />
    public void SaveProcessPlanOptions(ProcessPlanOptions options)
    {
        lock (_saveSync)
        {
            var root = ReadRoot();
            if (root["App"] is not JsonObject) root["App"] = new JsonObject();
            root["App"]!["processPlan"] = JsonSerializer.SerializeToNode(options, JsonOptions);
            WriteRoot(root);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private JsonObject ReadRoot()
        // 与IConfiguration一致忽略属性大小写，兼容现场使用App/ProcessPlan或camelCase的文件。
        => JsonNode.Parse(File.Exists(_settingsPath) ? File.ReadAllText(_settingsPath) : "{}",
            new JsonNodeOptions { PropertyNameCaseInsensitive = true }) as JsonObject
            ?? throw new InvalidOperationException("程序配置必须为JSON对象");

    // 原子替换避免文件监听读到半个JSON；同步重载保证保存成功后立即点击启动也读到新工艺。
    private void WriteRoot(JsonObject root)
    {
        var temporary = _settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, root.ToJsonString(JsonOptions));
            File.Move(temporary, _settingsPath, overwrite: true);
            if (_configuration is IConfigurationRoot configurationRoot) configurationRoot.Reload();
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
