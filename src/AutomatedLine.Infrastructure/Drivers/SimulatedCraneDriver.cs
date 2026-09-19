using AutomatedLine.Application.Abstractions;

namespace AutomatedLine.Infrastructure.Drivers;

/// <summary>
/// 模拟天车驱动。未接现场 PLC 时用于跑通搬运状态机。
/// 维护一份内存信号表，位置默认停在安全位 X，且"到位"为真。
/// </summary>
public sealed class SimulatedCraneDriver : ICraneDriver
{
    /// <summary>模拟信号表。</summary>
    private readonly Dictionary<string, object?> _values = new();

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="craneId">天车号</param>
    /// <param name="safeX">安全位 X，模拟位置从此处开始</param>
    public SimulatedCraneDriver(int craneId, double safeX)
    {
        CraneId = craneId;

        // 默认停在安全位、无料、无报警、到位
        _values["X"] = safeX;
        _values["Y"] = 0d;
        _values["Z"] = 0d;
        _values["InPosition"] = true;
        _values["Alarm"] = false;
        _values["HasMaterial"] = false;
        _values["CompanionSignal"] = 0;
        _values["TargetX"] = safeX;
        _values["TargetY"] = 0d;
        _values["TargetZ"] = 0d;
        _values["ActionWord"] = 0;
        _values["PickRequest"] = false;
        _values["PlaceRequest"] = false;
        _values["Grip"] = false;
    }

    /// <inheritdoc />
    public int CraneId { get; }

    /// <inheritdoc />
    public bool IsConnected { get; private set; }

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = true;
        Console.WriteLine($"[SimulatedCraneDriver] 天车{CraneId} 已连接");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = false;
        Console.WriteLine($"[SimulatedCraneDriver] 天车{CraneId} 已断开");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, object?>> ReadAllSignalsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, object?> snapshot = new Dictionary<string, object?>(_values);
        return Task.FromResult(snapshot);
    }

    /// <inheritdoc />
    public Task WriteSignalAsync(string pointName, object? value, CancellationToken cancellationToken = default)
    {
        _values[pointName] = value;
        Console.WriteLine($"[SimulatedCraneDriver] 写天车{CraneId}.{pointName} = {value}");
        return Task.CompletedTask;
    }
}
