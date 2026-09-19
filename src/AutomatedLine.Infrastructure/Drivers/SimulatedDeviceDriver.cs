using AutomatedLine.Application.Abstractions;

namespace AutomatedLine.Infrastructure.Drivers;

/// <summary>
/// 模拟设备驱动。用于未接现场 PLC 时跑通全流程（架构文档 §9.1）。
/// 不进行任何真实通信，只在内存里维护一份信号字典，读写都作用于它。
/// 真实协议实现后续按现场点位替换本类，业务层零改动。
/// </summary>
public sealed class SimulatedDeviceDriver : IDeviceDriver
{
    /// <summary>模拟信号表。</summary>
    private readonly Dictionary<string, object?> _values = new();

    /// <summary>
    /// 构造函数。初始化一组"设备空闲、允许取放"的默认信号。
    /// </summary>
    /// <param name="deviceId">设备号</param>
    public SimulatedDeviceDriver(int deviceId)
    {
        DeviceId = deviceId;

        // 默认：无料、无报警、不请求上/下料，但允许取放，便于后续状态机握手
        _values["HasMaterial"] = false;
        _values["Alarm"] = false;
        _values["RequestLoad"] = false;
        _values["RequestUnload"] = false;
        _values["AllowPick"] = true;
        _values["AllowPlace"] = true;
        _values["ProcessDone"] = false;
        _values["Clamp"] = false;
        _values["Release"] = false;
    }

    /// <inheritdoc />
    public int DeviceId { get; }

    /// <inheritdoc />
    public bool IsConnected { get; private set; }

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = true;
        Console.WriteLine($"[SimulatedDeviceDriver] 设备{DeviceId} 已连接");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = false;
        Console.WriteLine($"[SimulatedDeviceDriver] 设备{DeviceId} 已断开");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, object?>> ReadAllSignalsAsync(CancellationToken cancellationToken = default)
    {
        // 返回字典副本，保证调用方拿到的是一致快照（整体替换思想，架构文档 §8.2）
        IReadOnlyDictionary<string, object?> snapshot = new Dictionary<string, object?>(_values);
        return Task.FromResult(snapshot);
    }

    /// <inheritdoc />
    public Task WriteSignalAsync(string pointName, object? value, CancellationToken cancellationToken = default)
    {
        _values[pointName] = value;
        Console.WriteLine($"[SimulatedDeviceDriver] 写设备{DeviceId}.{pointName} = {value}");
        return Task.CompletedTask;
    }
}
