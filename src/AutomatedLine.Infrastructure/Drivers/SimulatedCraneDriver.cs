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

    // ── 命令方法（模拟：一律成功，供接口完整性与离线跑通流程）──

    /// <inheritdoc />
    public Task<CraneCommandResult> MoveAsync(int writeX, int writeY, int writeZ,
        int targetX, int targetY, int targetZ,
        bool pollEncoder, int zDirection, CancellationToken cancellationToken = default)
    {
        _values["InPosition"] = true;
        return Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.InPosition, $"天车[{CraneId}] 模拟到位"));
    }

    /// <inheritdoc />
    public Task<CraneCommandResult> JogAsync(char axis, int distance, bool positive, CancellationToken cancellationToken = default)
        => Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.Completed, $"天车[{CraneId}] 模拟点动下发"));

    /// <inheritdoc />
    public Task<CraneCommandResult> HomeAsync(char axis, CancellationToken cancellationToken = default)
        => Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.Completed, $"天车[{CraneId}] 模拟回原点"));

    /// <inheritdoc />
    public Task<CraneCommandResult> SetAbsoluteSpeedAsync(int speedX, int accelX, int decelX,
        int speedY, int accelY, int decelY, int speedZ, int accelZ, int decelZ,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.Completed, $"天车[{CraneId}] 模拟设置速度"));

    /// <inheritdoc />
    public Task<CraneCommandResult> MagnetOnAsync(CancellationToken cancellationToken = default)
    {
        _values["Magnetized"] = true;
        _values["HasPlate"] = true;
        _values["HasMaterial"] = true;
        return Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.Completed, $"天车[{CraneId}] 模拟充磁"));
    }

    /// <inheritdoc />
    public Task<CraneCommandResult> MagnetOffAsync(CancellationToken cancellationToken = default)
    {
        _values["Magnetized"] = false;
        _values["HasPlate"] = false;
        _values["HasMaterial"] = false;
        return Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.Completed, $"天车[{CraneId}] 模拟退磁"));
    }

    /// <inheritdoc />
    public Task<CraneCommandResult> DrainOpenAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.Completed, $"天车[{CraneId}] 模拟开接液盘"));

    /// <inheritdoc />
    public Task<CraneCommandResult> DrainCloseAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.Completed, $"天车[{CraneId}] 模拟关接液盘"));

    /// <inheritdoc />
    public Task<CraneCommandResult> ServoOnAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.Completed, $"天车[{CraneId}] 模拟伺服通电"));

    /// <inheritdoc />
    public Task<CraneCommandResult> ServoOffAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.Completed, $"天车[{CraneId}] 模拟伺服断电"));

    /// <inheritdoc />
    public Task<CraneCommandResult> SlowStopAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.Completed, $"天车[{CraneId}] 模拟减速停止"));

    /// <inheritdoc />
    public Task<CraneCommandResult> EmergencyStopAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.Completed, $"天车[{CraneId}] 模拟急停"));

    /// <inheritdoc />
    public Task<CraneCommandResult> ClearAlarmAsync(CancellationToken cancellationToken = default)
    {
        _values["Alarm"] = false;
        return Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.Completed, $"天车[{CraneId}] 模拟清除报警"));
    }
}
