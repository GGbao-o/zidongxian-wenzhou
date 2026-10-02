using AutomatedLine.Application.Abstractions;

namespace AutomatedLine.Tests.Fakes;

/// <summary>打桩天车驱动：记录动作顺序，信号由测试脚本化。</summary>
public sealed class FakeCraneDriver : ICraneDriver
{
    /// <summary>动作记录（如 "MOVE(1000,0,500)"、"MAGNET_ON"）。</summary>
    public List<string> Actions { get; } = new();

    /// <summary>当前状态（测试可改）。</summary>
    public CraneStatusDto? Status { get; set; } = new() { HasPlate = true };

    /// <inheritdoc />
    public int CraneId => 1;

    /// <inheritdoc />
    public bool IsConnected => true;

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, object?>> ReadAllSignalsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());

    /// <inheritdoc />
    public Task WriteSignalAsync(string pointName, object? value, CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<CraneStatusDto?> ReadStatusAsync(CancellationToken ct = default) => Task.FromResult(Status);

    /// <inheritdoc />
    public Task<CraneCommandResult> MoveAbsoluteAsync(int x, int y, int z, CancellationToken ct = default)
    {
        Actions.Add($"MOVE({x},{y},{z})");
        return Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.InPosition, "ok"));
    }

    /// <inheritdoc />
    public Task<CraneCommandResult> MoveRelativeAsync(int dx, int dy, int dz, CancellationToken ct = default)
    {
        Actions.Add($"REL({dx},{dy},{dz})");
        return Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.InPosition, "ok"));
    }

    /// <inheritdoc />
    public Task<CraneCommandResult> MagnetOnAsync(CancellationToken ct = default)
    {
        Actions.Add("MAGNET_ON");
        return Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.Completed, "ok"));
    }

    /// <inheritdoc />
    public Task<CraneCommandResult> MagnetOffAsync(CancellationToken ct = default)
    {
        Actions.Add("MAGNET_OFF");
        return Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.Completed, "ok"));
    }

    /// <inheritdoc />
    public Task<CraneCommandResult> MoveAsync(int writeX, int writeY, int writeZ, int targetX, int targetY, int targetZ, bool pollEncoder, int zDirection, CancellationToken ct = default)
        => Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.InPosition, "ok"));

    /// <inheritdoc />
    public Task<CraneCommandResult> JogAsync(char axis, int distance, bool positive, CancellationToken ct = default)
        => Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.Completed, "ok"));

    /// <inheritdoc />
    public Task<CraneCommandResult> HomeAsync(char axis, CancellationToken ct = default)
        => Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.Completed, "ok"));

    /// <inheritdoc />
    public Task<CraneCommandResult> SetAbsoluteSpeedAsync(int speedX, int accelX, int decelX, int speedY, int accelY, int decelY, int speedZ, int accelZ, int decelZ, CancellationToken ct = default)
    {
        Actions.Add("SET_SPEED");
        return Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.Completed, "ok"));
    }

    /// <inheritdoc />
    public Task<CraneCommandResult> DrainOpenAsync(CancellationToken ct = default)
        => Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.Completed, "ok"));

    /// <inheritdoc />
    public Task<CraneCommandResult> DrainCloseAsync(CancellationToken ct = default)
        => Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.Completed, "ok"));

    /// <inheritdoc />
    public Task<CraneCommandResult> ServoOnAsync(CancellationToken ct = default)
        => Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.Completed, "ok"));

    /// <inheritdoc />
    public Task<CraneCommandResult> ServoOffAsync(CancellationToken ct = default)
        => Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.Completed, "ok"));

    /// <inheritdoc />
    public Task<CraneCommandResult> SlowStopAsync(CancellationToken ct = default)
        => Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.Completed, "ok"));

    /// <inheritdoc />
    public Task<CraneCommandResult> EmergencyStopAsync(CancellationToken ct = default)
        => Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.Completed, "ok"));

    /// <inheritdoc />
    public Task<CraneCommandResult> ClearAlarmAsync(CancellationToken ct = default)
        => Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.Completed, "ok"));
}
