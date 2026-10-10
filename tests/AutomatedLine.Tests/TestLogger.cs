using System.Collections.Concurrent;
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Domain.Diagnostics;

namespace AutomatedLine.Tests;

/// <summary>
/// 测试用日志桩：支持多后台循环并发记录，断言只枚举稳定快照。
/// </summary>
public sealed class TestLogger : IAppLogger
{
    private readonly ConcurrentQueue<string> _messages = new();

    /// <summary>查询时刻的日志快照，后续写入不使正在执行的断言枚举失效。</summary>
    public IReadOnlyList<string> Messages => _messages.ToArray();

    /// <inheritdoc />
    public void Info(LogCategory category, string message) => _messages.Enqueue($"I:{category}:{message}");

    /// <inheritdoc />
    public void Warn(LogCategory category, string message) => _messages.Enqueue($"W:{category}:{message}");

    /// <inheritdoc />
    public void Error(LogCategory category, string message, Exception? exception = null)
        => _messages.Enqueue($"E:{category}:{message}");

    /// <inheritdoc />
    public void Sample(LogCategory category, string key, string message) => _messages.Enqueue($"S:{category}:{message}");
}
