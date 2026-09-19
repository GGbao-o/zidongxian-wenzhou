using AutomatedLine.Application.Abstractions;

namespace AutomatedLine.Infrastructure.Drivers;

/// <summary>
/// 模拟天车驱动。未接现场 PLC 时用于跑通搬运状态机与手动操作页。
/// 维护一份内存信号表：位置默认停在安全位 X；写入运动目标并置位"启动运动"后，模拟立即到位。
/// 现场接入后删除本类，改用真实驱动。
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

        // 坐标与运动相关
        _values["X"] = safeX;
        _values["Y"] = 0d;
        _values["Z"] = 0d;
        _values["InPosition"] = true;
        _values["TargetX"] = safeX;
        _values["TargetY"] = 0d;
        _values["TargetZ"] = 0d;
        _values["MoveMode"] = 0;
        _values["MoveSpeedX"] = 0d;
        _values["MoveAccelX"] = 0d;
        _values["MoveDecelX"] = 0d;
        _values["MoveSpeedY"] = 0d;
        _values["MoveAccelY"] = 0d;
        _values["MoveDecelY"] = 0d;
        _values["MoveSpeedZ"] = 0d;
        _values["MoveAccelZ"] = 0d;
        _values["MoveDecelZ"] = 0d;
        _values["MoveStart"] = false;

        // 状态与信号
        _values["Alarm"] = false;
        _values["ClearAlarm"] = false;
        _values["HasMaterial"] = false;
        _values["HasPlate"] = false;
        _values["CompanionSignal"] = 0;
        _values["Magnetized"] = false;
        _values["PressDownState"] = false;

        // 自动流程/手动动作命令位
        _values["ActionWord"] = 0;
        _values["PickRequest"] = false;
        _values["PlaceRequest"] = false;
        _values["Grip"] = false;
        _values["Magnetize"] = false;
        _values["Demagnetize"] = false;
        _values["PressDown"] = false;
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

        // 模拟 PLC 行为：让手动操作与命令位产生可见效果
        switch (pointName)
        {
            case "MoveStart" when value is true:
                ApplyPendingMove();
                break;
            case "ClearAlarm" when value is true:
                _values["Alarm"] = false;
                break;
            case "Magnetize" when value is true:
                _values["Magnetized"] = true;
                _values["HasPlate"] = true;
                _values["HasMaterial"] = true;
                break;
            case "Demagnetize" when value is true:
                _values["Magnetized"] = false;
                _values["HasPlate"] = false;
                _values["HasMaterial"] = false;
                break;
            case "PressDown" when value is true:
                _values["PressDownState"] = true;
                break;
        }

        Console.WriteLine($"[SimulatedCraneDriver] 写天车{CraneId}.{pointName} = {value}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 把当前目标坐标应用到实际坐标，并置"到位"。
    /// 真实 PLC 由伺服完成运动，这里模拟"立即到位"。
    /// </summary>
    private void ApplyPendingMove()
    {
        _values["X"] = Convert.ToDouble(_values["TargetX"]);
        _values["Y"] = Convert.ToDouble(_values["TargetY"]);
        _values["Z"] = Convert.ToDouble(_values["TargetZ"]);
        _values["InPosition"] = true;
    }
}
