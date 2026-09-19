using AutomatedLine.Application.Abstractions;
using AutomatedLine.Domain.Diagnostics;

namespace AutomatedLine.Tests;

/// <summary>
/// 测试用日志桩：把日志写进内存列表，供断言或排查。
/// </summary>
public sealed class TestLogger : IAppLogger
{
    /// <summary>采集到的日志。</summary>
    public List<string> Messages { get; } = new();

    /// <inheritdoc />
    public void Info(LogCategory category, string message) => Messages.Add($"I:{category}:{message}");

    /// <inheritdoc />
    public void Warn(LogCategory category, string message) => Messages.Add($"W:{category}:{message}");

    /// <inheritdoc />
    public void Error(LogCategory category, string message, Exception? exception = null)
        => Messages.Add($"E:{category}:{message}");

    /// <inheritdoc />
    public void Sample(LogCategory category, string key, string message) => Messages.Add($"S:{category}:{message}");
}
