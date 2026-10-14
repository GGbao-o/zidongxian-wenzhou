using AutomatedLine.Application.Abstractions;

namespace AutomatedLine.Tests.Fakes;

/// <summary>打桩设备驱动：内部信号字典，读/写均记账。</summary>
public sealed class FakeDeviceDriver : IDeviceDriver
{
    /// <summary>信号值（测试可预置/改）。</summary>
    public Dictionary<string, object?> Signals { get; } = new();

    /// <summary>写入记录。</summary>
    public List<string> Writes { get; } = new();

    /// <summary>脚本化写入时机或故障，用于核对到位通知、反馈等待与后续动作的顺序。</summary>
    public Func<string, object?, CancellationToken, Task>? OnWrite { get; set; }

    /// <summary>构造函数。</summary>
    /// <param name="deviceId">设备号</param>
    public FakeDeviceDriver(int deviceId) => DeviceId = deviceId;

    /// <inheritdoc />
    public int DeviceId { get; }

    /// <inheritdoc />
    public bool IsConnected => true;

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, object?>> ReadAllSignalsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>(Signals));

    /// <inheritdoc />
    public async Task WriteSignalAsync(string pointName, object? value, CancellationToken ct = default)
    {
        Writes.Add($"{pointName}={value}");
        if (OnWrite is not null) await OnWrite(pointName, value, ct);
        Signals[pointName] = value;
    }
}
