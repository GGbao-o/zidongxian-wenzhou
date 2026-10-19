using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Configuration;

namespace AutomatedLine.Tests.Fakes;

/// <summary>打桩配置服务：返回可控的 AppOptions。</summary>
public sealed class FakeConfigService : IConfigService
{
    /// <summary>程序参数。</summary>
    public AppOptions Options { get; set; } = new();

    /// <inheritdoc />
    public string GetConnectionString() => string.Empty;

    /// <inheritdoc />
    public AppOptions GetAppOptions() => Options;

    /// <inheritdoc />
    public void SaveAppOptions(AppOptions options) => Options = options;

    /// <summary>模拟工艺保存异常，用于验证失败时不发布新模板。</summary>
    public Exception? ProcessPlanSaveException { get; set; }

    /// <inheritdoc />
    public void SaveProcessPlanOptions(ProcessPlanOptions options)
    {
        if (ProcessPlanSaveException is not null) throw ProcessPlanSaveException;
        Options.ProcessPlan = options;
    }
}
