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
}
