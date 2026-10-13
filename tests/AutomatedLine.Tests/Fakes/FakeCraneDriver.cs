using AutomatedLine.Application.Abstractions;

namespace AutomatedLine.Tests.Fakes;

/// <summary>打桩天车驱动：记录动作顺序，信号由测试脚本化。</summary>
public sealed class FakeCraneDriver : ICraneDriver
{
    /// <summary>动作记录（如 "MOVE(1000,0,500)"、"MAGNET_ON"）。</summary>
    public List<string> Actions { get; } = new();

    /// <summary>当前状态（测试可改）。</summary>
    public CraneStatusDto? Status { get; set; } = new() { HasPlate = true };

    private int _statusReadCount;
    private int _hasPlateReadCount;

    /// <summary>专用X11实际读取次数，区分原位检查与每次补降后的检查。</summary>
    public int HasPlateReadCount => Volatile.Read(ref _hasPlateReadCount);

    /// <summary>脚本化专用X11结果或异常，验证无板补降不会用完整状态的有板缓存替代。</summary>
    public Func<CancellationToken, Task<bool>>? OnReadHasPlateAsync { get; set; }

    /// <summary>脚本化相对运动完成或失败，用于验证Z补降和取消边界。</summary>
    public Func<int, int, int, CancellationToken, Task<CraneCommandResult>>? OnRelativeMove { get; set; }

    /// <summary>完整状态实际读取次数，用于验证缓存消费不会重复触发通信。</summary>
    public int StatusReadCount => Volatile.Read(ref _statusReadCount);

    /// <summary>可脚本化完整状态读取，模拟慢通信、取消和恢复。</summary>
    public Func<CancellationToken, Task<CraneStatusDto?>>? OnReadStatus { get; set; }

    /// <summary>充磁指令开始下发后的结果，默认成功；可模拟反馈超时。</summary>
    public CraneCommandResult MagnetResult { get; set; } = CraneCommandResult.Ok(CraneCommandCode.Completed, "ok");

    /// <summary>充磁指令开始下发后的异常，用于验证通信异常与宿主取消的分支。</summary>
    public Exception? MagnetException { get; set; }

    /// <summary>单次 X11 读取异常，用于验证充磁前通信失败。</summary>
    public Exception? HasPlateException { get; set; }

    /// <summary>观察有板读取的时序和次数，不干扰运动动作记录。</summary>
    public Action? OnReadHasPlate { get; set; }

    /// <summary>指定绝对运动 X 目标的超时，用于验证去源设备和放料后收尾的异常。</summary>
    public int? FailAbsoluteMoveX { get; set; }

    /// <inheritdoc />
    public int CraneId { get; init; } = 1;

    /// <summary>绝对运动脚本，用于在收尾运动期间触发人工中止。</summary>
    public Func<int, CancellationToken, Task<CraneCommandResult>>? OnAbsoluteMove { get; set; }

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
    public Task<CraneStatusDto?> ReadStatusAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _statusReadCount);
        return OnReadStatus?.Invoke(ct) ?? Task.FromResult(Status);
    }

    /// <inheritdoc />
    public Task<bool> ReadHasPlateAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _hasPlateReadCount);
        OnReadHasPlate?.Invoke();
        if (OnReadHasPlateAsync is not null) return OnReadHasPlateAsync(ct);
        return HasPlateException is null
            ? Task.FromResult(Status?.HasPlate ?? false)
            : Task.FromException<bool>(HasPlateException);
    }

    /// <inheritdoc />
    public Task<CraneCommandResult> MoveAbsoluteAsync(int x, int y, int z, CancellationToken ct = default)
    {
        Actions.Add($"MOVE({x},{y},{z})");
        if (OnAbsoluteMove is not null) return OnAbsoluteMove(x, ct);
        if (x == FailAbsoluteMoveX)
        {
            return Task.FromResult(CraneCommandResult.Fail(CraneCommandCode.Timeout, "运动超时"));
        }
        return Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.InPosition, "ok"));
    }

    /// <inheritdoc />
    public Task<CraneCommandResult> MoveRelativeAsync(int dx, int dy, int dz, CancellationToken ct = default)
    {
        Actions.Add($"REL({dx},{dy},{dz})");
        if (OnRelativeMove is not null) return OnRelativeMove(dx, dy, dz, ct);
        return Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.InPosition, "ok"));
    }

    /// <inheritdoc />
    public Task<CraneCommandResult> MagnetOnAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Actions.Add("MAGNET_ON");
        return MagnetException is null ? Task.FromResult(MagnetResult) : Task.FromException<CraneCommandResult>(MagnetException);
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
