using System.Text.Json;
using System.Text.Json.Nodes;
using AutomatedLine.Application.Configuration;
using AutomatedLine.Domain.Routing;
using AutomatedLine.Infrastructure.Config;
using Microsoft.Extensions.Configuration;

namespace AutomatedLine.Tests;

/// <summary>在独立临时文件验证真实配置保存、立即重载及旧参数窗口不会覆盖新工艺。</summary>
public sealed class ProcessPlanConfigTests
{
    /// <summary>旧配置缺失微调上限时提供200/100默认值，常规参数保存后新上限可绑定且保留原连接串。</summary>
    [Fact]
    public void RefinementLimits_BindDefaultsAndPersistCustomValues()
    {
        using var h = new Fixture();
        var options = h.Store.GetAppOptions();
        Assert.Equal(200, options.Crane.XyRefineMaxXCorrectionMm);
        Assert.Equal(100, options.Crane.XyRefineMaxYCorrectionMm);
        options.Crane.XyRefineMaxXCorrectionMm = 160;
        options.Crane.XyRefineMaxYCorrectionMm = 80;
        h.Store.SaveAppOptions(options);
        using var restarted = (ConfigurationRoot)new ConfigurationBuilder().AddJsonFile(h.Path, false, false).Build();
        var config = new ConfigService(restarted, h.Path);
        Assert.Equal(160, config.GetAppOptions().Crane.XyRefineMaxXCorrectionMm);
        Assert.Equal(80, config.GetAppOptions().Crane.XyRefineMaxYCorrectionMm);
        Assert.Equal("original-job", config.GetAppOptions().ErpImport.FilePath);
        Assert.Equal("test-only", config.GetConnectionString());
    }

    /// <summary>工艺仅更新自身节点，立即读取和重建配置对象均能读到新值，其他字段及测试连接串保持原样。</summary>
    [Fact]
    public void SavePlan_IsImmediatelyVisible_AndPreservesOtherSettings()
    {
        using var h = new Fixture();
        var before = JsonNode.Parse(File.ReadAllText(h.Path))!;
        h.Store.SaveProcessPlanOptions(new ProcessPlanOptions { EnabledProcesses = new() { [ProcessType.Boring] = false } });
        Assert.False(h.Store.GetAppOptions().ProcessPlan.EnabledProcesses[ProcessType.Boring]);
        var after = JsonNode.Parse(File.ReadAllText(h.Path))!;
        Assert.True(JsonNode.DeepEquals(before["ConnectionStrings"], after["ConnectionStrings"]));
        Assert.True(JsonNode.DeepEquals(before["App"]!["erpImport"], after["App"]!["erpImport"]));
        Assert.True(JsonNode.DeepEquals(before["App"]!["customField"], after["App"]!["customField"]));
        Assert.True(JsonNode.DeepEquals(before["Untouched"], after["Untouched"]));
        using var restarted = (ConfigurationRoot)new ConfigurationBuilder().AddJsonFile(h.Path, false, false).Build();
        Assert.False(new ConfigService(restarted, h.Path).GetAppOptions().ProcessPlan.EnabledProcesses[ProcessType.Boring]);
    }

    /// <summary>常规参数窗口持有旧AppOptions时仍不能把最新工艺改回旧值。</summary>
    [Fact]
    public void StaleGeneralOptions_CannotOverwriteNewPlan()
    {
        using var h = new Fixture();
        var stale = h.Store.GetAppOptions();
        h.Store.SaveProcessPlanOptions(new ProcessPlanOptions { EnabledProcesses = new() { [ProcessType.Marking] = false } });
        stale.ErpImport.PollIntervalMs = 731;
        h.Store.SaveAppOptions(stale);
        var latest = h.Store.GetAppOptions();
        Assert.False(latest.ProcessPlan.EnabledProcesses[ProcessType.Marking]);
        Assert.Equal(731, latest.ErpImport.PollIntervalMs);
        Assert.Equal("original-job", latest.ErpImport.FilePath);
        Assert.Equal("test-only", h.Store.GetConnectionString());
    }

    /// <summary>配置绑定接受不同大小写，局部保存和旧参数保存也必须保持同一节点，避免生成重复配置或丢失工艺。</summary>
    /// <param name="appKey">现场App节点大小写。</param>
    /// <param name="planKey">现场工艺节点大小写。</param>
    [Theory]
    [InlineData("App", "ProcessPlan")]
    [InlineData("app", "PROCESSPLAN")]
    public void MixedCaseNodes_AreUpdatedAndPreserved(string appKey, string planKey)
    {
        using var h = new Fixture();
        var root = JsonNode.Parse(File.ReadAllText(h.Path))!.AsObject();
        var app = root["App"]!.DeepClone();
        root.Remove("App");
        root[appKey] = app;
        app[planKey] = JsonNode.Parse("""{"EnabledProcesses":{"Marking":false}}""");
        File.WriteAllText(h.Path, root.ToJsonString());
        h.Configuration.Reload();
        var stale = h.Store.GetAppOptions();
        Assert.False(stale.ProcessPlan.EnabledProcesses[ProcessType.Marking]);
        h.Store.SaveProcessPlanOptions(new ProcessPlanOptions { EnabledProcesses = new() { [ProcessType.Boring] = false } });
        h.Store.SaveAppOptions(stale);
        Assert.False(h.Store.GetAppOptions().ProcessPlan.EnabledProcesses[ProcessType.Boring]);
        Assert.Equal("test-only", h.Store.GetConnectionString());
        using var restarted = (ConfigurationRoot)new ConfigurationBuilder().AddJsonFile(h.Path, false, false).Build();
        Assert.False(new ConfigService(restarted, h.Path).GetAppOptions().ProcessPlan.EnabledProcesses[ProcessType.Boring]);
    }

    /// <summary>现有文件损坏时不覆盖原数据，也不遗留临时文件。</summary>
    [Fact]
    public void InvalidExistingJson_IsNotOverwritten()
    {
        using var h = new Fixture();
        File.WriteAllText(h.Path, "broken-json");
        Assert.ThrowsAny<JsonException>(() => h.Store.SaveProcessPlanOptions(new ProcessPlanOptions()));
        Assert.Equal("broken-json", File.ReadAllText(h.Path));
        Assert.Single(Directory.GetFiles(h.Directory));
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AutomatedLine-Plan-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public ConfigurationRoot Configuration { get; }
        public ConfigService Store { get; }

        public Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            Path = System.IO.Path.Combine(Directory, "appsettings.json");
            File.WriteAllText(Path, """
                {"ConnectionStrings":{"MySql":"test-only"},"App":{"erpImport":{"filePath":"original-job","pollIntervalMs":500},"customField":{"keep":true}},"Untouched":{"value":7}}
                """);
            Configuration = (ConfigurationRoot)new ConfigurationBuilder().AddJsonFile(Path, false, false).Build();
            Store = new ConfigService(Configuration, Path);
        }

        public void Dispose()
        {
            Configuration.Dispose();
            System.IO.Directory.Delete(Directory, true);
        }
    }
}
