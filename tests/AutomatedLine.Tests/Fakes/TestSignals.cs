using AutomatedLine.Domain.Signals;

namespace AutomatedLine.Tests.Fakes;

/// <summary>
/// 测试用信号快照构造：模拟 DeviceStateCollector 写入的缓存快照。
/// </summary>
public static class TestSignals
{
    /// <summary>用若干"点名=值"构造一条已连接快照。</summary>
    /// <param name="items">点名与值</param>
    /// <returns>信号快照</returns>
    public static SignalSnapshot Of(params (string Key, object? Value)[] items)
        => new(DateTime.Now, 1, ConnectionState.Connected, SignalQuality.Good,
            items.ToDictionary(i => i.Key, i => i.Value));
}
